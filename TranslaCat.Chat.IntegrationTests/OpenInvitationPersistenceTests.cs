using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenInvitations;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.OpenInvitations;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class OpenInvitationPersistenceTests(ChatMySqlFixture database)
{
    private static DateTime Now => ChatMySqlFixture.Epoch;
    private static DateTime Utc => DateTime.SpecifyKind(Now, DateTimeKind.Utc);

    [Fact]
    public async Task Active_regular_member_creates_unlisted_room_invitation_without_join_or_identity_disclosure()
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        var delivery = new Delivery(database);

        // 실행
        var result = await Store(delivery).CreateAsync(actor, room, Request(actor + 1), default);

        // 검증: 실제 초대 메시지와 별도 push 의도는 함께 저장되고 membership은 변하지 않는다.
        await using var db = await database.Contexts.CreateDbContextAsync();
        var notification = await db.ChatNotifications.SingleAsync(row => row.Id == result.InvitationId);
        var outbox = await db.OpenInvitationPushOutbox.SingleAsync(row => row.NotificationId == notification.Id);
        Assert.Equal(actor + 1, notification.RecipientUserId);
        Assert.Equal(room, outbox.RoomId);
        Assert.Equal(actor, outbox.SenderUserId);
        Assert.Equal(actor + 1, outbox.RecipientUserId);
        Assert.False(result.Duplicate);
        Assert.False(await db.ChatRoomMembers.AnyAsync(row => row.ChatRoomId == room && row.UserId == actor + 1));
        using var payload = JsonDocument.Parse(notification.PayloadJson);
        Assert.Equal("방 전용 별명", payload.RootElement.GetProperty("inviterOpenNickname").GetString());
        Assert.Equal("OPEN_LINK", payload.RootElement.GetProperty("invitationKind").GetString());
        Assert.Equal(6, payload.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("sensitive", notification.PayloadJson);
        Assert.DoesNotContain(actor.ToString(), notification.PayloadJson);
        Assert.Equal([notification.Id], delivery.Delivered.ToArray());
    }

    [Fact]
    public async Task Concurrent_same_request_commits_one_card_one_outbox_and_one_queue_delivery()
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        var request = Request(actor + 1);
        var delivery = new Delivery(database);

        // 실행
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store(delivery)
            .CreateAsync(actor, room, request, default)));

        // 검증
        Assert.Single(results, row => !row.Duplicate);
        Assert.Single(results.Select(row => row.InvitationId).Distinct());
        Assert.Single(delivery.Delivered);
        await using var db = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await db.ChatNotifications.CountAsync(row => row.ActorUserId == actor));
        Assert.Equal(1, await db.OpenInvitationPushOutbox.CountAsync(row => row.SenderUserId == actor));
        Assert.Equal(1, await db.OpenInvitationSenderGates.CountAsync(row => row.ActorUserId == actor));
    }

    [Fact]
    public async Task Request_id_cannot_be_rebound_to_another_target_or_room_and_completed_duplicate_survives_closure()
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        var otherRoom = await SeedAsync(actor);
        var request = Request(actor + 1);
        var store = Store(new Delivery(database));
        var original = await store.CreateAsync(actor, room, request, default);

        // 실행 / 검증
        foreach (var (targetRoom, input) in new[] { (otherRoom, request), (room, request with { TargetPublicId = PublicId(actor + 2) }) })
        {
            var error = await Assert.ThrowsAsync<OpenInvitationException>(() => store.CreateAsync(actor, targetRoom, input, default));
            Assert.Equal("OPEN_INVITATION_REQUEST_CONFLICT", error.Code);
        }
        await using (var db = await database.Contexts.CreateDbContextAsync())
            await db.OpenChatRooms.Where(row => row.ChatRoomId == room).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "CLOSED"));
        var duplicate = await store.CreateAsync(actor, room, request, default);
        Assert.Equal(original.InvitationId, duplicate.InvitationId);
        Assert.True(duplicate.Duplicate);
    }

    [Fact]
    public async Task New_request_id_cannot_bypass_pair_cooldown_but_allows_resend_after_60_seconds()
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        DateTime current = Now;
        var store = Store(new Delivery(database), clock: () => (current, DateTime.SpecifyKind(current, DateTimeKind.Utc)));
        await store.CreateAsync(actor, room, Request(actor + 1), default);

        // 실행 / 검증
        current = Now.AddSeconds(59);
        var error = await Assert.ThrowsAsync<OpenInvitationException>(() => store.CreateAsync(actor, room,
            Request(actor + 1), default));
        Assert.Equal("OPEN_INVITATION_RATE_LIMITED", error.Code);
        current = Now.AddSeconds(61);
        var resent = await store.CreateAsync(actor, room, Request(actor + 1), default);
        Assert.False(resent.Duplicate);
    }

    [Fact]
    public async Task Gate_and_directory_wait_use_fresh_clock_for_rate_limit_and_persisted_push_ttl()
    {
        // 준비: 첫 요청이 actor gate를 가진 채 외부 계정 조회에서 기다리는 동안 두 번째 요청을 시작한다.
        var actor = UserId();
        var room = await SeedAsync(actor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        DateTime current = Now;
        var directory = new Directory { BeforeLookup = async () =>
        {
            if (Interlocked.Increment(ref lookups) == 1) { entered.SetResult(); await release.Task; }
        } };
        var store = Store(new Delivery(database), directory,
            () => (current, DateTime.SpecifyKind(current, DateTimeKind.Utc)));
        var first = store.CreateAsync(actor, room, Request(actor + 1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = store.CreateAsync(actor, room, Request(actor + 1), default);

        // 실행: 요청 시작보다 2분 뒤에 저장하게 하여 오래된 캡처 시각을 사용하면 실패하도록 한다.
        current = Now.AddMinutes(2);
        release.SetResult();
        var created = await first;
        var error = await Assert.ThrowsAsync<OpenInvitationException>(async () => await waiting);

        // 검증: 실제 저장 시각에서 pair 제한이 유지되고 푸시 TTL도 그 시각부터 시작한다.
        Assert.Equal("OPEN_INVITATION_RATE_LIMITED", error.Code);
        Assert.Equal(current, created.CreatedAt);
        await using var db = await database.Contexts.CreateDbContextAsync();
        var job = await db.OpenInvitationPushOutbox.SingleAsync(row => row.NotificationId == created.InvitationId);
        Assert.Equal(DateTime.SpecifyKind(current, DateTimeKind.Utc), job.CreatedAtUtc);
        Assert.Equal(job.CreatedAtUtc, job.DueAtUtc);
    }

    [Fact]
    public async Task Actor_gate_enforces_rolling_total_limit_across_different_rooms_and_concurrent_requests()
    {
        // 준비
        var actor = UserId();
        var first = await SeedAsync(actor);
        var second = await SeedAsync(actor);
        var delivery = new Delivery(database);

        // 실행
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 14).Select(async index =>
        {
            try { await Store(delivery).CreateAsync(actor, index % 2 == 0 ? first : second, Request(actor + index), default); return "created"; }
            catch (OpenInvitationException error) { return error.Code; }
        }));

        // 검증
        Assert.Equal(10, outcomes.Count(value => value == "created"));
        Assert.Equal(4, outcomes.Count(value => value == "OPEN_INVITATION_RATE_LIMITED"));
        await using var db = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(10, await db.OpenInvitationPushOutbox.CountAsync(row => row.SenderUserId == actor));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("blocked")]
    [InlineData("already-member")]
    [InlineData("banned")]
    public async Task Ineligible_target_reasons_share_one_error_and_persist_nothing(string reason)
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        var directory = new Directory { Blocked = reason == "blocked" };
        long target = reason == "self" ? actor : actor + 1;
        if (reason is "already-member" or "banned")
        {
            await using var db = await database.Contexts.CreateDbContextAsync();
            var member = new ChatRoomMemberEntity { ChatRoomId = room, UserId = target, Role = "MEMBER",
                Active = reason == "already-member", JoinedAt = Now, CreatedAt = Now, UpdatedAt = Now };
            db.ChatRoomMembers.Add(member);
            await db.SaveChangesAsync();
            if (reason == "banned")
            {
                var sender = await db.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == room && row.UserId == actor);
                db.OpenChatBans.Add(new OpenChatBanEntity { ChatRoomId = room, TargetUserId = target,
                    TargetChatRoomMemberId = member.Id, TargetMemberCode = "OC-BAN", NicknameSnapshot = "별명",
                    LastJoinedAtSnapshot = Now, TargetRoleSnapshot = "MEMBER", BannedByMemberId = sender.Id,
                    BannedByRole = "OWNER", BannedAt = Now, Reason = "합성", CreatedAt = Now, UpdatedAt = Now });
                await db.SaveChangesAsync();
            }
        }
        var request = Request(target) with { TargetPublicId = reason == "missing" ? "absent" : PublicId(target) };

        // 실행 / 검증
        var error = await Assert.ThrowsAsync<OpenInvitationException>(() => Store(new Delivery(database), directory)
            .CreateAsync(actor, room, request, default));
        Assert.Equal("OPEN_INVITATION_TARGET_UNAVAILABLE", error.Code);
        await using var read = await database.Contexts.CreateDbContextAsync();
        Assert.False(await read.ChatNotifications.AnyAsync(row => row.ActorUserId == actor));
        Assert.False(await read.OpenInvitationPushOutbox.AnyAsync(row => row.SenderUserId == actor));
        Assert.False(await read.OpenInvitationSenderGates.AnyAsync(row => row.ActorUserId == actor));
    }

    [Fact]
    public async Task Queue_failure_after_commit_is_recovered_by_duplicate_and_does_not_create_again()
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        var request = Request(actor + 1);
        var delivery = new Delivery(database) { FailDelivery = true };

        // 실행
        var created = await Store(delivery).CreateAsync(actor, room, request, default);
        var duplicate = await Store(delivery).CreateAsync(actor, room, request, default);

        // 검증
        Assert.Equal(created.InvitationId, duplicate.InvitationId);
        Assert.True(duplicate.Duplicate);
        Assert.Single(delivery.Delivered);
        Assert.NotNull(await Store(delivery).GetAsync(actor + 1, created.InvitationId, default));
    }

    [Theory]
    [InlineData("left", "OPEN_INVITATION_MEMBER_ACCESS_DENIED")]
    [InlineData("closed", "OPEN_INVITATION_ROOM_CLOSED")]
    [InlineData("inactive", "OPEN_INVITATION_NOT_ALLOWED")]
    public async Task Current_sender_and_room_authority_is_required_before_creating_card(string changed, string code)
    {
        // 준비
        var actor = UserId();
        var room = await SeedAsync(actor);
        await using (var db = await database.Contexts.CreateDbContextAsync())
        {
            if (changed == "left") await db.ChatRoomMembers.Where(row => row.ChatRoomId == room && row.UserId == actor)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Active, false));
            if (changed == "closed") await db.OpenChatRooms.Where(row => row.ChatRoomId == room)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "CLOSED"));
            if (changed == "inactive") await db.ChatRooms.Where(row => row.Id == room)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Active, false));
        }

        // 실행 / 검증
        var error = await Assert.ThrowsAsync<OpenInvitationException>(() => Store(new Delivery(database))
            .CreateAsync(actor, room, Request(actor + 1), default));
        Assert.Equal(code, error.Code);
        await using var read = await database.Contexts.CreateDbContextAsync();
        Assert.False(await read.ChatNotifications.AnyAsync(row => row.ActorUserId == actor));
        Assert.False(await read.OpenInvitationPushOutbox.AnyAsync(row => row.SenderUserId == actor));
    }

    [Fact]
    public async Task Recipient_filter_precedes_pagination_and_read_cannot_modify_other_kinds_or_accounts()
    {
        // 준비
        var actor = UserId();
        var firstRoom = await SeedAsync(actor);
        var secondRoom = await SeedAsync(actor);
        var store = Store(new Delivery(database));
        var first = await store.CreateAsync(actor, firstRoom, Request(actor + 1), default);
        var second = await store.CreateAsync(actor, secondRoom, Request(actor + 1), default);
        long otherKind;
        await using (var db = await database.Contexts.CreateDbContextAsync())
        {
            var activity = new ChatNotificationEntity { RecipientUserId = actor + 1, NotificationType = "OPEN_CHAT_ROLE_CHANGED",
                ChatRoomId = firstRoom, PayloadJson = "{}", SourceEventKey = "synthetic:" + Guid.NewGuid(), CreatedAt = Now };
            db.ChatNotifications.Add(activity);
            await db.SaveChangesAsync();
            otherKind = activity.Id;
        }

        // 실행 / 검증
        var page = await store.ListAsync(actor + 1, null, 1, default);
        Assert.Equal(second.InvitationId, Assert.Single(page).Id);
        Assert.Equal(first.InvitationId, Assert.Single(await store.ListAsync(actor + 1, page[0].Id, 1, default)).Id);
        Assert.Null(await store.GetAsync(actor + 2, first.InvitationId, default));
        Assert.Null(await store.ReadAsync(actor + 2, first.InvitationId, Now, default));
        Assert.Null(await store.ReadAsync(actor + 1, otherKind, Now, default));
        var read = await store.ReadAsync(actor + 1, first.InvitationId, Now.AddSeconds(1), default);
        var readAgain = await store.ReadAsync(actor + 1, first.InvitationId, Now.AddSeconds(5), default);
        Assert.True(read!.IsRead);
        Assert.Equal(read.ReadAt, readAgain!.ReadAt);
        var summary = await store.SummaryAsync(actor + 1, default);
        Assert.Equal(1, summary.UnreadCount);
        Assert.Equal(second.InvitationId, summary.LatestInvitation!.Id);
    }

    private EfOpenInvitationStore Store(Delivery delivery, Directory? directory = null, Func<(DateTime, DateTime)>? clock = null) =>
        new(database.Contexts, directory ?? new(), delivery, NullLogger<EfOpenInvitationStore>.Instance, clock ?? (() => (Now, Utc)));
    private static long UserId() => Random.Shared.NextInt64(20_000_000_000, 25_000_000_000);
    private static string PublicId(long id) => "TC-" + id;
    private static OpenInvitationValidatedCreate Request(long target) => new(PublicId(target), Guid.NewGuid());
    private async Task<long> SeedAsync(long actor)
    {
        var seeded = await database.SeedReadRoomAsync("OPEN", actor);
        await using var db = await database.Contexts.CreateDbContextAsync();
        db.OpenChatRooms.Add(new OpenChatRoomEntity { ChatRoomId = seeded.RoomId, Visibility = "UNLISTED", Status = "ACTIVE",
            MaxMemberCount = 100, CreatedAt = Now, UpdatedAt = Now });
        db.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity { ChatRoomMemberId = seeded.MemberId,
            MemberCode = "OC-" + Guid.NewGuid().ToString("N")[..10], Nickname = "방 전용 별명", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();
        return seeded.RoomId;
    }
    private sealed class Directory : IChatMembershipDirectory
    {
        public bool Blocked { get; init; }
        public Func<Task>? BeforeLookup { get; init; }
        public Task<ChatMembershipUser?> FindByIdAsync(long id, CancellationToken token) => Task.FromResult<ChatMembershipUser?>(
            new(id, "sensitive@example.invalid", "sensitive-name", PublicId(id), "sensitive-general-profile", null));
        public async Task<ChatMembershipUser?> FindByPublicIdAsync(string id, CancellationToken token)
        {
            if (BeforeLookup is not null) await BeforeLookup();
            return id.StartsWith("TC-") && long.TryParse(id[3..], out long parsed) ? await FindByIdAsync(parsed, token) : null;
        }
        public Task<bool> AreFriendsAsync(long id, long other, CancellationToken token) => Task.FromResult(false);
        public Task<bool> IsBlockedBetweenAsync(long id, long other, CancellationToken token) => Task.FromResult(Blocked);
    }
    private sealed class Delivery(ChatMySqlFixture database) : IChatActivityNotificationDelivery
    {
        public bool FailDelivery { get; init; }
        public ConcurrentQueue<long> Delivered { get; } = new();
        public Task ValidateAvailabilityAsync(CancellationToken token) => Task.CompletedTask;
        public async Task DeliverAsync(string email, ChatNotificationActivity notification, DateTime now, CancellationToken token)
        {
            await using var db = await database.Contexts.CreateDbContextAsync(token);
            Assert.True(await db.ChatNotifications.AnyAsync(row => row.Id == notification.Id, token));
            Assert.True(await db.OpenInvitationPushOutbox.AnyAsync(row => row.NotificationId == notification.Id, token));
            Delivered.Enqueue(notification.Id);
            if (FailDelivery) throw new InvalidOperationException("Synthetic delivery unavailable");
        }
    }
}
