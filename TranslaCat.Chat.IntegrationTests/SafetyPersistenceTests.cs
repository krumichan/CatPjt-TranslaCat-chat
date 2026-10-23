using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Api.Safety;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;
using TranslaCat.Chat.Infrastructure.Push;
using TranslaCat.Chat.Infrastructure.Safety;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed partial class SafetyPersistenceTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Open_report_resolves_identity_internally_and_preserves_duplicate_receipt()
    {
        // 준비: 실제 migrated CHAT catalog의 합성 OPEN membership과 원문만 사용한다.
        var seed = await SeedAsync(41001, "OPEN");
        var store = new EfChatReportStore(fixture.Contexts, new SyntheticProfiles());
        var request = new ChatReportCreateRequest("MESSAGE", seed.MessageId, null, null, "HARASSMENT", "합성 신고 설명", Guid.NewGuid().ToString("D"));

        // 실행
        var first = await store.CreateAsync(seed.UserId, seed.RoomId, request, Now, default);
        var duplicate = await store.CreateAsync(seed.UserId, seed.RoomId, request, Now, default);
        var detail = await store.GetAsync(first.Id, Now, default);

        // 검증: 공개 receipt/detail에는 전역 계정 ID가 없고 제한 증거 행만 실제 대상을 가진다.
        Assert.Equal(first.Id, duplicate.Id);
        Assert.True(duplicate.Duplicate);
        Assert.Equal("합성 원문 body", detail.EvidenceContent);
        Assert.Equal("합성 OPEN", detail.EvidenceSenderDisplay);
        string receiptJson = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("TargetUserId", receiptJson);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(seed.UserId + 1, (await db.ChatReports.SingleAsync(row => row.Id == first.Id)).TargetUserId);
        Assert.Equal(1, await db.ChatReports.CountAsync(row => row.ReporterUserId == seed.UserId));
        var conflict = await Assert.ThrowsAsync<ChatSafetyException>(() => store.CreateAsync(seed.UserId, seed.RoomId,
            request with { Detail = "다른 내용" }, Now, default));
        Assert.Equal("CHAT_REPORT_REQUEST_CONFLICT", conflict.Code);
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.CreateAsync(seed.UserId, seed.RoomId,
            request with { ClientRequestId = Guid.NewGuid().ToString("D"), TargetType = "MEMBER", MessageId = null, TargetUserId = seed.UserId + 1 }, Now, default));
    }

    [Fact]
    public async Task Report_scope_rejects_self_outside_room_and_before_join_but_accepts_AI()
    {
        // 준비
        var seed = await SeedAsync(42001);
        var store = new EfChatReportStore(fixture.Contexts, new SyntheticProfiles());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var mine = ChatMySqlFixture.Message(seed.RoomId, seed.UserId, Now);
        var before = ChatMySqlFixture.Message(seed.RoomId, seed.UserId + 1, ChatMySqlFixture.Epoch.AddSeconds(-1));
        var ai = ChatMySqlFixture.Message(seed.RoomId, null, Now);
        ai.SenderType = "AI";
        db.ChatMessages.AddRange(mine, before, ai);
        await db.SaveChangesAsync();
        ChatReportCreateRequest Request(long id) => new("MESSAGE", id, null, null, "SPAM", null, Guid.NewGuid().ToString("D"));

        // 실행 / 검증
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.CreateAsync(seed.UserId, seed.RoomId, Request(mine.Id), Now, default));
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.CreateAsync(seed.UserId, seed.RoomId, Request(before.Id), Now, default));
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.CreateAsync(seed.UserId + 99, seed.RoomId, Request(seed.MessageId), Now, default));
        Assert.Equal("PENDING", (await store.CreateAsync(seed.UserId, seed.RoomId, Request(ai.Id), Now, default)).Status);
    }

    [Fact]
    public async Task Review_version_and_90_day_expiry_preserve_shared_original_but_purge_evidence()
    {
        // 준비
        var seed = await SeedAsync(43001);
        var store = new EfChatReportStore(fixture.Contexts, new SyntheticProfiles());
        var receipt = await store.CreateAsync(seed.UserId, seed.RoomId,
            new("MESSAGE", seed.MessageId, null, null, "OTHER", "합성 비공개 설명", Guid.NewGuid().ToString("D")), Now, default);

        // 실행: 검토 후 같은 이전 버전의 덮어쓰기를 거절하고 만료 직후 읽기와 실제 정리를 모두 확인한다.
        var reviewed = await store.UpdateAsync(49001, receipt.Id, new("IN_REVIEW", "합성 검토 메모", 0), Now.AddMinutes(1), default);
        Assert.Equal(1, reviewed.Receipt.Version);
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.UpdateAsync(49002, receipt.Id, new("RESOLVED", "오래된 수정", 0), Now.AddMinutes(2), default));
        var expired = await store.GetAsync(receipt.Id, Now.AddDays(90), default);
        await store.PurgeExpiredEvidenceAsync(Now.AddDays(90), default);

        // 검증
        Assert.True(expired.Receipt.EvidenceExpired);
        Assert.Null(expired.Detail);
        Assert.Null(expired.EvidenceContent);
        Assert.Null(expired.ReviewNote);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var row = await db.ChatReports.SingleAsync(row => row.Id == receipt.Id);
        Assert.Null(row.TargetUserId);
        Assert.Null(row.ReporterUserId);
        Assert.Null(row.EvidenceContent);
        Assert.NotNull(row.EvidencePurgedAt);
        Assert.Null((await db.ChatReportReviews.SingleAsync(row => row.ReportId == receipt.Id)).ReviewerUserId);
        Assert.Equal("합성 원문 body", (await db.ChatMessages.SingleAsync(row => row.Id == seed.MessageId)).Content);
    }

    [Fact]
    public async Task Block_masks_rest_and_realtime_translation_and_excludes_badge_without_changing_cursor_or_original()
    {
        // 준비
        var seed = await SeedAsync(44001);
        var handler = new CoreHandler(true);
        var policy = new ChatContentPolicy(fixture.Contexts, Core(handler));
        var transaction = new EfChatMessageTransaction(fixture.Contexts, new SyntheticProfiles(), new NoDelivery(), NullLogger<EfChatMessageTransaction>.Instance, policy);
        var service = new ChatMessageService(transaction, () => Now);

        // 실행
        var page = await service.GetMessagesAsync(seed.UserId, seed.RoomId);
        var row = Assert.Single(page.Messages);
        var notifications = new EfChatNotificationStore(fixture.Contexts, policy);
        var badge = await notifications.GetSummaryAsync(seed.UserId, default);
        var realtime = new ChatRealtimeContentPolicy(fixture.Contexts, policy);
        string json = JsonSerializer.Serialize(new { eventType = "chat.message.created", message = new { id = seed.MessageId,
            content = "합성 원문 body", senderUserId = seed.UserId + 1, senderName = "합성 이름", senderEmail = "synthetic@example.invalid", translations = new[] { new { translatedContent = "translated private" } } } });
        string masked = (await realtime.PrepareAsync(seed.UserId, seed.RoomId, json, default))!;
        string? translated = await realtime.PrepareAsync(seed.UserId, seed.RoomId,
            JsonSerializer.Serialize(new { eventType = "chat.translation.completed", messageId = seed.MessageId, translatedContent = "translated private" }), default);

        // 검증: 같은 message ID와 pagination 경계를 유지하며 DB 원문/번역은 별도 보존한다.
        Assert.Equal(seed.MessageId, row.Id);
        Assert.Equal("BLOCKED", row.ContentVisibility);
        Assert.Empty(row.Translations);
        Assert.Null(row.SenderUserId);
        Assert.DoesNotContain("합성 원문 body", masked);
        Assert.DoesNotContain("synthetic@example.invalid", masked);
        Assert.Null(translated);
        Assert.Equal(0, badge.UnreadChatMessageCount);
        handler.Blocked = false;
        Assert.Equal("합성 원문 body", Assert.Single((await service.GetMessagesAsync(seed.UserId, seed.RoomId)).Messages).Content);
        Assert.Equal(1, (await notifications.GetSummaryAsync(seed.UserId, default)).UnreadChatMessageCount);
    }

    [Fact]
    public async Task Actual_shared_websocket_masks_only_blocked_recipient_and_never_delivers_late_translation_to_them()
    {
        // 준비: 실제 MySQL membership 두 수신자와 실제 Kestrel/STOMP broker를 사용한다.
        var seed = await SeedAsync(44501);
        long visibleUserId = seed.UserId + 2;
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            setup.ChatRoomMembers.Add(new() { ChatRoomId = seed.RoomId, UserId = visibleUserId, Role = "MEMBER",
                JoinedAt = ChatMySqlFixture.Epoch, CreatedAt = Now, UpdatedAt = Now });
            await setup.SaveChangesAsync();
        }
        var handler = new CoreHandler(true) { OnlyBlockedRecipient = seed.UserId };
        var policy = new ChatContentPolicy(fixture.Contexts, Core(handler));
        await using var host = await SafetyRealtimeHost.StartAsync(fixture.Contexts, policy);
        using var blocked = await host.ConnectAsync(seed.UserId, seed.RoomId);
        using var visible = await host.ConnectAsync(visibleUserId, seed.RoomId);
        string created = JsonSerializer.Serialize(new { eventType = "chat.message.created", chatRoomId = seed.RoomId,
            message = new { id = seed.MessageId, senderUserId = seed.UserId + 1, senderEmail = "private-sender@example.invalid",
                senderName = "private sender", senderProfileImageUrl = "https://example.invalid/private.png", content = "private source text",
                translations = new[] { new { translatedContent = "private translation" } } } });

        // 실행: 같은 shared publish를 수신자별로 판정한다.
        await host.Broker.PublishRoomAsync(seed.RoomId, created);
        string hiddenFrame = await blocked.ReceiveAsync();
        string visibleFrame = await visible.ReceiveAsync();

        // 검증: marker와 별개로 wire에 원문·번역·계정 식별자가 없어야 한다.
        Assert.Contains("\"contentVisibility\":\"BLOCKED\"", hiddenFrame);
        Assert.DoesNotContain("private source text", hiddenFrame);
        Assert.DoesNotContain("private translation", hiddenFrame);
        Assert.DoesNotContain("private-sender@example.invalid", hiddenFrame);
        Assert.DoesNotContain("private.png", hiddenFrame);
        Assert.Contains("\"senderUserId\":null", hiddenFrame);
        Assert.Contains("\"contentVisibility\":\"VISIBLE\"", visibleFrame);
        Assert.Contains("private source text", visibleFrame);

        string completed = JsonSerializer.Serialize(new { eventType = "chat.translation.completed", chatRoomId = seed.RoomId,
            messageId = seed.MessageId, translatedContent = "late private translation" });
        await host.Broker.PublishRoomAsync(seed.RoomId, completed);
        Assert.Contains("late private translation", await visible.ReceiveAsync());
        await blocked.AssertBarrierAsync("no-hidden-translation");

        // 차단 해제는 기존 DB 원문을 복원하고 같은 socket의 구독을 유지한다.
        handler.Blocked = false;
        await host.Broker.PublishRoomAsync(seed.RoomId, created);
        Assert.Contains("private source text", await blocked.ReceiveAsync());
        Assert.Contains("private source text", await visible.ReceiveAsync());
    }

    [Fact]
    public async Task Send_lock_wait_and_slow_relation_never_restore_deleted_profile_or_private_invitation()
    {
        // 준비: 관계 provider를 기다리는 실제 송신 하나가 socket lock을 잡는다. 과거 profile/초대 DTO는 뒤에서 대기한다.
        var seed = await SeedAsync(44701, "OPEN");
        long notificationId;
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            var notification = new ChatNotificationEntity { RecipientUserId = seed.UserId, ActorUserId = seed.UserId + 1,
                NotificationType = "CHAT_INVITATION", ChatRoomId = seed.RoomId, SourceEventKey = "late-private:" + Guid.NewGuid().ToString("N"),
                PayloadJson = "{\"inviterOpenNickname\":\"private old nickname\"}", CreatedAt = Now };
            setup.ChatNotifications.Add(notification);
            await setup.SaveChangesAsync();
            notificationId = notification.Id;
        }
        var handler = new CoreHandler(false) { PauseRelations = true };
        var policy = new ChatContentPolicy(fixture.Contexts, Core(handler));
        await using var host = await SafetyRealtimeHost.StartAsync(fixture.Contexts, policy);
        using var socket = await host.ConnectAsync(seed.UserId, seed.RoomId);
        await socket.SubscribeAsync("private", "/user/queue/chat/notifications");
        string created = JsonSerializer.Serialize(new { eventType = "chat.message.created", chatRoomId = seed.RoomId,
            message = new { id = seed.MessageId, senderUserId = seed.UserId + 1, senderEmail = "private-old@example.invalid",
                senderName = "private old nickname", content = "shared original", translations = Array.Empty<object>() } });
        Task first = host.Broker.PublishRoomAsync(seed.RoomId, created);
        await handler.RelationReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task profile = host.Broker.PublishRoomAsync(seed.RoomId, JsonSerializer.Serialize(new
        {
            eventType = "chat.open-profile.updated", roomId = seed.RoomId, openChatMemberId = seed.OtherMemberId,
            nickname = "private old nickname", profileImageUrl = "https://example.invalid/private-old.png"
        }));
        Task invitation = host.Broker.PublishUserAsync($"safety-{seed.UserId}@example.invalid", "/queue/chat/notifications",
            JsonSerializer.Serialize(new { eventType = "chat.notification.created", notification = new { id = notificationId,
                payload = new { inviterOpenNickname = "private old nickname" } } }));

        // 실행: 대기 중 source 계정의 cleanup을 완료한 뒤 과거 외부 응답/DTO를 풀어 준다.
        var cleanup = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime, new FixedTime());
        Assert.Equal("COMPLETED", (await cleanup.DeleteAsync(seed.UserId + 1,
            new(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), null), default)).Status);
        handler.RelationResume.TrySetResult();
        await Task.WhenAll(first, profile, invitation).WaitAsync(TimeSpan.FromSeconds(10));

        // 검증: Core 대기 뒤 재검사된 메시지는 익명이고, 뒤늦은 profile/private 초대는 wire에 없다.
        string delivered = await socket.ReceiveAsync();
        Assert.Contains("\"authorDeleted\":true", delivered);
        Assert.DoesNotContain("private old nickname", delivered);
        Assert.DoesNotContain("private-old@example.invalid", delivered);
        Assert.Contains("shared original", delivered);
        await socket.AssertBarrierAsync("no-stale-profile-or-invitation");
    }

    [Fact]
    public async Task Account_cleanup_is_idempotent_transfers_owner_suppresses_push_and_anonymizes_without_deleting_text()
    {
        // 준비
        var seed = await SeedAsync(45001, "OPEN");
        var deletedId = seed.UserId + 1;
        string audit = "delete-synthetic@example.invalid";
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            var room = await setup.ChatRooms.SingleAsync(row => row.Id == seed.RoomId);
            room.OwnerId = deletedId;
            room.CreatedBy = audit;
            var target = await setup.ChatRoomMembers.SingleAsync(row => row.UserId == deletedId && row.ChatRoomId == seed.RoomId);
            target.Role = "OWNER";
            (await setup.ChatMessages.SingleAsync(row => row.Id == seed.MessageId)).CreatedBy = audit;
            setup.ChatPushOutbox.Add(new() { MessageId = seed.MessageId, RoomId = seed.RoomId, SenderUserId = deletedId,
                RecipientUserId = seed.UserId, EventId = Guid.NewGuid().ToString("D"), CreatedAtUtc = Now, DueAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        var time = new FixedTime();
        var store = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime, time);
        var request = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), audit);

        // 실행
        var first = await store.DeleteAsync(deletedId, request, default);
        var second = await store.DeleteAsync(deletedId, request, default);

        // 검증: 공유 본문을 보존하고 기존 이름/이메일/프로필 및 늦은 수신을 제거한다.
        Assert.Equal("COMPLETED", first.Status);
        Assert.Equal(first, second);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(seed.UserId, (await db.ChatRooms.SingleAsync(row => row.Id == seed.RoomId)).OwnerId);
        var message = await db.ChatMessages.SingleAsync(row => row.Id == seed.MessageId);
        Assert.Equal("합성 원문 body", message.Content);
        Assert.Null(message.CreatedBy);
        Assert.False((await db.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == seed.RoomId && row.UserId == deletedId)).Active);
        Assert.Equal("탈퇴한 사용자", (await db.OpenChatMemberProfiles.SingleAsync(row => row.ChatRoomMemberId == seed.OtherMemberId)).Nickname);
        Assert.Equal("SUPPRESSED", (await db.ChatPushOutbox.SingleAsync(row => row.MessageId == seed.MessageId)).Status);
        var policy = new ChatContentPolicy(fixture.Contexts, Core(new CoreHandler(false)));
        await Assert.ThrowsAsync<ChatSafetyException>(() => policy.RequireActiveAsync(deletedId, default));
        var transaction = new EfChatMessageTransaction(fixture.Contexts, new SyntheticProfiles(), new NoDelivery(), NullLogger<EfChatMessageTransaction>.Instance, policy);
        var response = Assert.Single((await new ChatMessageService(transaction, () => Now).GetMessagesAsync(seed.UserId, seed.RoomId)).Messages);
        Assert.True(response.AuthorDeleted);
        Assert.Null(response.SenderUserId);
        Assert.Null(response.SenderEmail);
        Assert.Equal("탈퇴한 사용자", response.SenderName);
        Assert.Equal("합성 원문 body", response.Content);
        await Assert.ThrowsAsync<ChatSafetyException>(() => store.DeleteAsync(deletedId, request with { OperationId = Guid.NewGuid().ToString("D") }, default));
    }

    [Fact]
    public async Task Storage_failure_keeps_pending_until_exact_queued_object_is_deleted()
    {
        // 준비
        var seed = await SeedAsync(46001, "OPEN");
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            var profile = await setup.OpenChatMemberProfiles.SingleAsync(row => row.ChatRoomMemberId == seed.OtherMemberId);
            profile.ProfileImageObjectKey = $"open-chat/owned-synthetic-{seed.OtherMemberId}.webp";
            await setup.SaveChangesAsync();
        }
        var handler = new CoreHandler(false) { StorageAvailable = false };
        var time = new FixedTime();
        var store = new EfChatAccountDeletionStore(fixture.Contexts, Core(handler), instant => instant.UtcDateTime, time);
        var request = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), null);

        // 실행 / 검증: 외부 삭제 실패를 완료로 표시하지 않고 같은 operation으로 다시 이어간다.
        Assert.Equal("PENDING", (await store.DeleteAsync(seed.UserId + 1, request, default)).Status);
        time.Instant = time.Instant.AddHours(2);
        handler.StorageAvailable = true;
        Assert.Equal("COMPLETED", (await store.DeleteAsync(seed.UserId + 1, request, default)).Status);
        Assert.Equal(2, handler.StorageCalls);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatAccountDeletionObjects.AnyAsync(row => row.UserId == seed.UserId + 1));
    }

    [Fact]
    public async Task Account_write_gate_waits_for_prior_commit_and_rejects_reinsertion_after_cleanup()
    {
        // 준비: 동일 계정의 이전 mutation이 계정 gate를 가진 상태로 실제 MySQL transaction을 연다.
        var seed = await SeedAsync(47001);
        long deletedId = seed.UserId + 1;
        await using var writer = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await ChatAccountWriteGate.AcquireAsync(writer, [deletedId], default);
        var lateRoom = new ChatRoomEntity { RoomType = "GROUP", OwnerId = deletedId, Name = "삭제 이전 합성 작성", CreatedAt = Now.AddSeconds(30), UpdatedAt = Now.AddSeconds(30), CreatedBy = "fence-synthetic@example.invalid" };
        writer.ChatRooms.Add(lateRoom);
        await writer.SaveChangesAsync();
        var cleanupTime = new FixedTime();
        var deletion = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime, cleanupTime);
        var request = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), "fence-synthetic@example.invalid");

        // 실행: 정리는 같은 계정 gate에서 대기하며 이전 commit 뒤 새 방까지 현재 상태로 정리한다.
        Task<ChatAccountDeletionResult> pending = deletion.DeleteAsync(deletedId, request, default);
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        cleanupTime.Instant = cleanupTime.Instant.AddMinutes(1);
        await transaction.CommitAsync();
        Assert.Equal("COMPLETED", (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);

        // 검증: 후속 join/invite/profile/new-room 저장자가 같은 gate를 잡으면 업무 쓰기 전 tombstone을 본다.
        await using var reader = await fixture.Contexts.CreateDbContextAsync();
        var goneRoom = await reader.ChatRooms.SingleAsync(row => row.Id == lateRoom.Id);
        Assert.False(goneRoom.Active);
        Assert.Null(goneRoom.OwnerId);
        Assert.Null(goneRoom.CreatedBy);
        // BE 예약 해제 뒤 동일 이메일로 만든 새 행은, 이미 local-complete인 이전 작업의 재시도가 건드리지 않는다.
        var newAccountRoom = new ChatRoomEntity { RoomType = "GROUP", OwnerId = seed.UserId + 99,
            CreatedAt = Now.AddMinutes(2), UpdatedAt = Now.AddMinutes(2), CreatedBy = "fence-synthetic@example.invalid" };
        reader.ChatRooms.Add(newAccountRoom);
        await reader.SaveChangesAsync();
        cleanupTime.Instant = cleanupTime.Instant.AddMinutes(2);
        Assert.Equal("COMPLETED", (await deletion.DeleteAsync(deletedId, request, default)).Status);
        await reader.Entry(newAccountRoom).ReloadAsync();
        Assert.Equal("fence-synthetic@example.invalid", newAccountRoom.CreatedBy);
        await using var after = await fixture.Contexts.CreateDbContextAsync();
        await using var afterTransaction = await after.Database.BeginTransactionAsync();
        var failure = await Assert.ThrowsAsync<ChatSafetyException>(() => ChatAccountWriteGate.AcquireAsync(after, [deletedId], default));
        Assert.Equal("CHAT_ACCOUNT_DELETED", failure.Code);
        Assert.Equal(1, await reader.ChatAccountDeletions.CountAsync(row => row.UserId == deletedId));
    }

    [Fact]
    public async Task Actual_room_creation_finishes_before_cleanup_and_cannot_recreate_deleted_membership()
    {
        // 준비: 실제 room store가 계정 gate를 가진 뒤 audit 의존성에서 잠시 기다리게 한다.
        var seed = await SeedAsync(48001);
        long deletedId = seed.UserId + 1;
        var accounts = new PausingRoomAccounts();
        var policy = new ChatContentPolicy(fixture.Contexts, Core(new CoreHandler(false)));
        var rooms = new EfChatRoomStore(fixture.Contexts, accounts, contentPolicy: policy);
        var request = new ChatRoomCreateRequest("GROUP", "정리 전 대기한 방", null, [seed.UserId]);
        Task<long> creation = rooms.CreateOrReuseAsync(deletedId, request, [seed.UserId], Now.AddSeconds(30), default);
        await accounts.AuditReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cleanupTime = new FixedTime { Instant = new DateTimeOffset(Now.AddMinutes(1), TimeSpan.Zero) };
        var cleanup = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime, cleanupTime);
        var operation = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), "room-fence@example.invalid");

        // 실행: account gate가 실제 생성과 정리를 직렬화한다.
        Task<ChatAccountDeletionResult> deletion = cleanup.DeleteAsync(deletedId, operation, default);
        await Task.Delay(150);
        Assert.False(deletion.IsCompleted);
        accounts.Resume.TrySetResult();
        long createdId = await creation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("COMPLETED", (await deletion.WaitAsync(TimeSpan.FromSeconds(10))).Status);

        // 검증: 승계된 공유 방/다른 사람 데이터는 유지하며, 탈퇴 membership은 비활성이고 감사 메일은 제거된다.
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var room = await read.ChatRooms.SingleAsync(row => row.Id == createdId);
        Assert.Equal(seed.UserId, room.OwnerId);
        Assert.Null(room.CreatedBy);
        Assert.False((await read.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == createdId && row.UserId == deletedId)).Active);
        var failure = await Assert.ThrowsAsync<ChatSafetyException>(() => rooms.CreateOrReuseAsync(deletedId,
            request with { Name = "정리 후 금지" }, [seed.UserId], Now.AddMinutes(2), default));
        Assert.Equal("CHAT_ACCOUNT_DELETED", failure.Code);
        Assert.False(await read.ChatRooms.AnyAsync(row => row.Name == "정리 후 금지"));
    }

    [Fact]
    public async Task Completed_translation_retry_never_exposes_blocked_text_and_restores_after_unblock()
    {
        // 준비: 이미 완료된 번역도 retry API 응답을 통해 우회 노출되면 안 된다.
        var seed = await SeedAsync(48501);
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            setup.ChatMessageTranslations.Add(new() { ChatMessageId = seed.MessageId, LanguageCode = "ja",
                Status = "COMPLETED", TranslatedContent = "합성 비공개 번역", CompletedAt = Now, CreatedAt = Now, UpdatedAt = Now });
            await setup.SaveChangesAsync();
        }
        var handler = new CoreHandler(true);
        var policy = new ChatContentPolicy(fixture.Contexts, Core(handler));
        var translations = new EfChatTranslationStore(fixture.Contexts, () => Now, new SyntheticProfiles(), policy);

        // 실행 / 검증: worker 실행 여부와 관계없이 차단된 완료 결과를 반환하지 않는다.
        var failure = await Assert.ThrowsAsync<ChatMessageException>(() => translations.RetryAsync(seed.UserId,
            seed.RoomId, seed.MessageId, "ja", false, Now, default));
        Assert.Equal("CHAT_CONTENT_BLOCKED", failure.ErrorCode);
        handler.Blocked = false;
        var restored = await translations.RetryAsync(seed.UserId, seed.RoomId, seed.MessageId, "ja", false, Now, default);
        Assert.Equal("합성 비공개 번역", restored.Translation.TranslatedContent);
        Assert.Null(restored.Intent);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var stored = await read.ChatMessageTranslations.SingleAsync(row => row.ChatMessageId == seed.MessageId);
        Assert.Equal("COMPLETED", stored.Status);
        Assert.Equal("합성 비공개 번역", stored.TranslatedContent);
    }

    [Fact]
    public async Task Actual_translation_retry_serializes_with_cleanup_and_cannot_reinsert_late_audit_email()
    {
        // 준비: 실제 retry transaction이 계정 gate/번역 row를 잡고 감사 조회에서 기다린다.
        var seed = await SeedAsync(48601);
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            setup.ChatMessageTranslations.Add(new() { ChatMessageId = seed.MessageId, LanguageCode = "ja",
                Status = "FAILED", TranslatedContent = "이전 공유 번역", CreatedAt = Now, UpdatedAt = Now });
            await setup.SaveChangesAsync();
        }
        var profiles = new PausingProfiles();
        var policy = new ChatContentPolicy(fixture.Contexts, Core(new CoreHandler(false)));
        var translations = new EfChatTranslationStore(fixture.Contexts, () => Now, profiles, policy);
        var retry = translations.RetryAsync(seed.UserId, seed.RoomId, seed.MessageId, "ja", true, Now.AddSeconds(30), default);
        await profiles.AuditReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cleanup = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime,
            new FixedTime { Instant = new DateTimeOffset(Now.AddMinutes(1), TimeSpan.Zero) });
        var operation = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), "retry-fence@example.invalid");

        // 실행: 요청 시각 뒤 끝나는 retry도 cleanup이 기다린 다음 감사 메일을 지운다.
        Task<ChatAccountDeletionResult> pending = cleanup.DeleteAsync(seed.UserId, operation, default);
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        profiles.Resume.TrySetResult();
        Assert.NotNull((await retry.WaitAsync(TimeSpan.FromSeconds(10))).Intent);
        Assert.Equal("COMPLETED", (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);

        // 검증: 공유 번역은 남고 개인정보는 제거되며 완료 뒤 늦은 호출은 gate에서 거절된다.
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var row = await read.ChatMessageTranslations.SingleAsync(value => value.ChatMessageId == seed.MessageId);
        Assert.Null(row.UpdatedBy);
        Assert.Equal("이전 공유 번역", row.TranslatedContent);
        var failure = await Assert.ThrowsAsync<ChatSafetyException>(() => translations.RetryAsync(seed.UserId,
            seed.RoomId, seed.MessageId, "ja", true, Now.AddMinutes(2), default));
        Assert.Equal("CHAT_ACCOUNT_DELETED", failure.Code);
        await read.Entry(row).ReloadAsync();
        Assert.Null(row.UpdatedBy);
    }

    private static readonly DateTime Now = DateTime.SpecifyKind(ChatMySqlFixture.Epoch.AddHours(2), DateTimeKind.Utc);
    private async Task<Seed> SeedAsync(long userId, string type = "GROUP")
    {
        // 같은 소유 catalog를 재사용해도 앞선 실행의 tombstone/읽음 데이터와 계정이 섞이지 않는다.
        userId += RandomNumberGenerator.GetInt32(100000, 2000000000) * 1000000L;
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var room = new ChatRoomEntity { RoomType = type, SourceType = type == "OPEN" ? "OPEN" : "MANUAL", OwnerId = userId, CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch };
        db.ChatRooms.Add(room);
        await db.SaveChangesAsync();
        var mine = new ChatRoomMemberEntity { ChatRoomId = room.Id, UserId = userId, Role = "OWNER", JoinedAt = ChatMySqlFixture.Epoch, CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch };
        var other = new ChatRoomMemberEntity { ChatRoomId = room.Id, UserId = userId + 1, Role = "MEMBER", JoinedAt = ChatMySqlFixture.Epoch, CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch };
        db.ChatRoomMembers.AddRange(mine, other);
        var message = ChatMySqlFixture.Message(room.Id, userId + 1, ChatMySqlFixture.Epoch.AddMinutes(1));
        message.Content = "합성 원문 body";
        db.ChatMessages.Add(message);
        if (type == "OPEN") db.OpenChatRooms.Add(new() { ChatRoomId = room.Id, Visibility = "PUBLIC", MaxMemberCount = 100, Status = "ACTIVE", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();
        if (type == "OPEN")
        {
            db.OpenChatMemberProfiles.AddRange(new OpenChatMemberProfileEntity { ChatRoomMemberId = mine.Id, MemberCode = "OC-" + mine.Id, Nickname = "합성 신고자", CreatedAt = Now, UpdatedAt = Now },
                new OpenChatMemberProfileEntity { ChatRoomMemberId = other.Id, MemberCode = "OC-" + other.Id, Nickname = "합성 OPEN", CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
        }
        return new(room.Id, userId, message.Id, other.Id);
    }
    private sealed record Seed(long RoomId, long UserId, long MessageId, long OtherMemberId);
    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Instant { get; set; } = new(Now, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Instant;
    }
    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken token) => Task.FromResult(new ChatUserMessageProfile(userId, "합성 사용자", "synthetic@example.invalid", null));
        public Task<string?> ResolveObjectUrlAsync(string key, CancellationToken token) => Task.FromResult<string?>(null);
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token) => Task.FromResult("synthetic@example.invalid");
    }
    private sealed class PausingRoomAccounts : IChatRoomAccountReader
    {
        public TaskCompletionSource AuditReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token) => Task.CompletedTask;
        public async Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
        {
            AuditReached.TrySetResult();
            await Resume.Task.WaitAsync(token);
            return "room-fence@example.invalid";
        }
        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token) =>
            Task.FromResult(new ChatDirectPartner(userId, "SYNTHETIC", "합성", null, null, null, null));
    }
    private sealed class PausingProfiles : IChatMessageProfileReader
    {
        public TaskCompletionSource AuditReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken token) =>
            Task.FromResult(new ChatUserMessageProfile(userId, "합성", "retry-fence@example.invalid", null));
        public Task<string?> ResolveObjectUrlAsync(string key, CancellationToken token) => Task.FromResult<string?>(null);
        public async Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
        {
            AuditReached.TrySetResult();
            await Resume.Task.WaitAsync(token);
            return "retry-fence@example.invalid";
        }
    }
    private sealed class NoDelivery : IChatMessageEventDelivery
    {
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(ChatMessageIntent intent, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Tokens : IChatCoreAccessTokenProvider { public string CreateToken(string scope) => "synthetic-service-token"; }
    private static HttpChatCoreClient Core(CoreHandler handler) => new(new HttpClient(handler), new(new Uri("http://127.0.0.1:1"), TimeSpan.FromSeconds(2)), new Tokens());
    private sealed class CoreHandler(bool blocked) : HttpMessageHandler
    {
        public bool Blocked { get; set; } = blocked;
        public long? OnlyBlockedRecipient { get; set; }
        public bool PauseRelations { get; set; }
        public TaskCompletionSource RelationReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RelationResume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StorageAvailable { get; set; } = true;
        public int StorageCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Delete)
            {
                StorageCalls++;
                return new(StorageAvailable ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable);
            }
            if (PauseRelations)
            {
                RelationReached.TrySetResult();
                await RelationResume.Task.WaitAsync(token);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var ids = body.RootElement.GetProperty("targetUserIds").EnumerateArray().Select(value => value.GetInt64()).ToArray();
            bool isBlocked = Blocked && (OnlyBlockedRecipient is null || body.RootElement.GetProperty("requesterUserId").GetInt64() == OnlyBlockedRecipient);
            string json = JsonSerializer.Serialize(new { relations = ids.Select(id => new { targetUserId = id, friends = false, blocked = isBlocked, friendStatus = isBlocked ? "BLOCKED" : "NONE" }), missingUserIds = Array.Empty<long>() });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
