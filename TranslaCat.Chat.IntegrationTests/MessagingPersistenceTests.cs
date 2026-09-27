using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.AiManagement;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class MessagingPersistenceTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Text_and_pending_translation_commit_before_any_delivery()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await AddMemberAsync(room, room.UserId + 1);
        var delivery = new RecordingDelivery(fixture);

        // 실행
        var message = await Service(delivery).CreateTextAsync(room.UserId, room.RoomId, "  合成 한글 😺  ");

        // 검증 — delivery마다 독립 connection에서 메시지가 보이는지 확인한다.
        Assert.Equal("合成 한글 😺", message.Content);
        Assert.Equal(1, message.UnreadMemberCount);
        var translation = Assert.Single(message.Translations);
        Assert.Equal("ja", translation.LanguageCode);
        Assert.Equal("PENDING", translation.Status);
        Assert.Null(translation.TranslatedContent);
        Assert.Collection(delivery.Attempted,
            value => Assert.IsType<ChatMessageCreatedIntent>(value),
            value => Assert.IsType<ChatTranslationRequestedIntent>(value),
            value => Assert.IsType<ChatHumanMessageRecordedIntent>(value),
            value => Assert.IsType<ChatAiTriggerRequestedIntent>(value));
        Assert.All(delivery.ObservedCommittedStates, Assert.True);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatMessages.SingleAsync(value => value.Id == message.Id);
        Assert.Equal("synthetic@example.invalid", row.CreatedBy);
        Assert.Equal(ChatMySqlFixture.Epoch.AddMinutes(1), row.CreatedAt);
        Assert.Equal(DateTimeKind.Unspecified, row.CreatedAt.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_profile_or_event_dependency_rolls_back_flushed_rows(bool missingDelivery)
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        var delivery = new RecordingDelivery(fixture) { Missing = missingDelivery };
        var profiles = new SyntheticProfiles { Missing = !missingDelivery };

        // 실행
        await Assert.ThrowsAsync<ChatMessageDependencyUnavailableException>(() =>
            Service(delivery, profiles).CreateTextAsync(room.UserId, room.RoomId, "rollback synthetic"));

        // 검증 — 실제 save/flush 후 예외가 나도 본문과 번역 행을 모두 되돌린다.
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await context.ChatMessages.CountAsync(value => value.ChatRoomId == room.RoomId));
        Assert.False(await context.ChatMessageTranslations.AnyAsync(value =>
            context.ChatMessages.Any(message => message.Id == value.ChatMessageId && message.ChatRoomId == room.RoomId)));
        Assert.Empty(delivery.Attempted);
    }

    [Fact]
    public async Task Nonmember_cannot_read_or_create()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        var delivery = new RecordingDelivery(fixture);
        var service = Service(delivery);

        // 실행
        var read = await Assert.ThrowsAsync<ChatMessageException>(() => service.GetMessagesAsync(-1, room.RoomId));
        var write = await Assert.ThrowsAsync<ChatMessageException>(() => service.CreateTextAsync(-1, room.RoomId, "denied"));

        // 검증
        Assert.Equal("CHAT_ROOM_MEMBER_ACCESS_DENIED", read.ErrorCode);
        Assert.Equal(read.ErrorCode, write.ErrorCode);
        Assert.Empty(delivery.Attempted);
    }

    [Fact]
    public async Task Closed_open_room_allows_history_hides_identity_and_denies_creation()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync("OPEN");
        var sender = await AddMemberAsync(room, room.UserId + 1);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Visibility = "PUBLIC",
                MaxMemberCount = 10,
                Status = "CLOSED",
                ClosedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            context.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
            {
                ChatRoomMemberId = sender.Id,
                MemberCode = "m" + sender.Id,
                Nickname = "익명 합성 발신자",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        var delivery = new RecordingDelivery(fixture);
        var service = Service(delivery, new SyntheticProfiles { Missing = true });

        // 실행
        var page = await service.GetMessagesAsync(room.UserId, room.RoomId);
        var error = await Assert.ThrowsAsync<ChatMessageException>(() => service.CreateTextAsync(room.UserId, room.RoomId, "closed"));

        // 검증 — OPEN history는 일반 계정 프로필 port를 호출하지 않는다.
        Assert.Equal("OPEN_CHAT_ROOM_CLOSED", error.ErrorCode);
        Assert.All(page.Messages, message =>
        {
            Assert.Null(message.SenderUserId);
            Assert.Null(message.SenderName);
            Assert.Null(message.SenderEmail);
            Assert.Null(message.SenderProfileImageUrl);
            Assert.Equal(sender.Id, message.Sender!.OpenChatMemberId);
            Assert.Equal("익명 합성 발신자", message.Sender.Nickname);
        });
        Assert.Empty(delivery.Attempted);
    }

    [Fact]
    public async Task Real_queries_preserve_join_boundary_status_soft_delete_and_pagination_directions()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        ChatMessageEntity[] visible;
        long inaccessibleId;
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            visible = Enumerable.Range(1, 101).Select(index =>
                ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddSeconds(index + 2))).ToArray();
            context.ChatMessages.AddRange(visible);
            var beforeJoin = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddTicks(-10));
            var deleted = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddMinutes(3));
            deleted.DeletedAt = ChatMySqlFixture.Epoch.AddMinutes(4);
            var unsent = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddMinutes(3));
            unsent.Status = "DELETED";
            context.ChatMessages.AddRange(beforeJoin, deleted, unsent);
            await context.SaveChangesAsync();
            inaccessibleId = beforeJoin.Id;
        }
        var service = Service(new RecordingDelivery(fixture));

        // 실행
        var previous = await service.GetMessagesAsync(room.UserId, room.RoomId);
        var after = await service.GetMessagesAfterAsync(room.UserId, room.RoomId, room.FirstId, 2);
        var anchor = await service.GetMessagesAroundAnchorAsync(room.UserId, room.RoomId, visible[50].Id, 1, 1);
        var inaccessible = await Assert.ThrowsAsync<ChatMessageException>(() =>
            service.GetMessagesAfterAsync(room.UserId, room.RoomId, inaccessibleId));

        // 검증
        Assert.Equal(visible.Skip(1).Select(message => message.Id), previous.Messages.Select(message => message.Id));
        Assert.Equal(visible[1].Id, previous.NextCursorId);
        Assert.True(previous.HasNext);
        Assert.Equal([room.SecondId, visible[0].Id], after.Messages.Select(message => message.Id));
        Assert.Equal(visible[0].Id, after.NextCursorId);
        Assert.Equal(visible.Skip(49).Take(3).Select(message => message.Id), anchor.Messages.Select(message => message.Id));
        Assert.Equal("CHAT_MESSAGE_FORWARD_CURSOR_NOT_ACCESSIBLE", inaccessible.ErrorCode);
    }

    [Fact]
    public async Task Delivery_failure_keeps_commit_and_attempts_remaining_intents()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        var delivery = new RecordingDelivery(fixture) { FailFirst = true };

        // 실행
        var message = await Service(delivery).CreateTextAsync(room.UserId, room.RoomId, "delivery failure synthetic");

        // 검증 — 실제 외부 전송 보장이 아니라 DB commit과 adapter 호출의 경계를 검증한다.
        Assert.Equal(4, delivery.Attempted.Count);
        Assert.All(delivery.ObservedCommittedStates, Assert.True);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.True(await context.ChatMessages.AnyAsync(value => value.Id == message.Id));
    }

    [Fact]
    public async Task Group_send_and_AI_settings_use_room_before_member_lock_and_both_commit()
    {
        // 준비: Application service와 각 독립 EF transaction/connection은 실제 구현이다.
        // 계정 조회와 이벤트 전달만 테스트 대역이며 HTTP/실시간 전송 검증으로 확대하지 않는다.
        var room = await fixture.SeedReadRoomAsync("GROUP", Random.Shared.NextInt64(87000000, 88000000));
        await using (var seed = await fixture.Contexts.CreateDbContextAsync())
        {
            await seed.ChatRoomMembers.Where(member => member.Id == room.MemberId)
                .ExecuteUpdateAsync(update => update.SetProperty(member => member.Role, "OWNER"));
        }
        var profiles = new SyntheticProfiles { AuditGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var messages = new RecordingDelivery(fixture);
        var settingsEvents = new RecordingAiSettingsEvents();
        var management = new EfChatAiManagementStore(fixture.Contexts, settingsEvents,
            NullLogger<EfChatAiManagementStore>.Instance, new SyntheticRoomAccount());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ChatMessageView> send = Service(messages, profiles)
            .CreateTextAsync(room.UserId, room.RoomId, "GROUP lock ordering synthetic", deadline.Token);
        Task<ChatAiRoomSettings>? patch = null;

        try
        {
            // 실행: send가 room/member lock을 보유한 뒤 감사 조회 port에서 INSERT 직전 멈춘다.
            await profiles.ReachedAudit.Task.WaitAsync(deadline.Token);
            patch = management.RoomSettingsAsync(room.UserId, room.RoomId,
                new ChatAiRoomPatch("PRIVATE", null, false, null), ChatMySqlFixture.Epoch.AddMinutes(2), deadline.Token);

            // 별도 관측 connection으로 실제 InnoDB 대기를 확인한다. 단순 Delay로 경합을 추측하지 않는다.
            // 과거 member→room 역전도 member 대기로 관측되므로 release 뒤 실제 deadlock 회귀를 잡을 수 있다.
            await using var observer = await fixture.Contexts.CreateDbContextAsync(deadline.Token);
            string roomKey = room.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string memberKey = room.MemberId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            while (await observer.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM performance_schema.data_lock_waits w
                JOIN performance_schema.data_locks l ON l.ENGINE_LOCK_ID = w.REQUESTING_ENGINE_LOCK_ID
                WHERE l.OBJECT_SCHEMA = DATABASE() AND l.INDEX_NAME = 'PRIMARY'
                  AND ((l.OBJECT_NAME = 'chat_room' AND l.LOCK_DATA = {roomKey})
                    OR (l.OBJECT_NAME = 'chat_room_member' AND l.LOCK_DATA = {memberKey}))
                """).SingleAsync(deadline.Token) == 0)
            {
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(send.IsCompleted);
            Assert.False(patch.IsCompleted);
            profiles.AuditGate.SetResult(true);

            var sent = await send;
            var setting = await patch;

            // 검증: 두 업무가 deadlock 없이 commit되고 send 이벤트에서 독립 connection의 저장 상태가 보인다.
            Assert.Equal("PRIVATE", setting.DisclosureType);
            Assert.False(setting.ConversationEnabled);
            Assert.IsType<OpenMembersChanged>(Assert.Single(settingsEvents.Delivered));
            Assert.All(messages.ObservedCommittedStates, Assert.True);
            Assert.Equal(4, messages.Attempted.Count);
            Assert.True(await observer.ChatMessages.AnyAsync(message => message.Id == sent.Id, deadline.Token));
            Assert.Equal(3, await observer.ChatMessages.CountAsync(message => message.ChatRoomId == room.RoomId, deadline.Token));
            var stored = await observer.ChatRoomAiSettings.SingleAsync(row => row.ChatRoomId == room.RoomId, deadline.Token);
            Assert.Equal("PRIVATE", stored.DisclosureType);
            Assert.False(stored.ConversationEnabled);
        }
        finally
        {
            // assertion 실패에도 대역 barrier나 DB transaction을 다음 테스트에 남기지 않는다.
            profiles.AuditGate.TrySetResult(true);
            deadline.Cancel();
            Task[] pending = patch is null ? [send] : [send, patch];
            foreach (Task task in pending)
            {
                try
                {
                    await task;
                }
                catch (Exception) when (task.IsFaulted || task.IsCanceled) { }
            }
        }
    }

    [Fact]
    public async Task Cancellation_during_precommit_dependency_check_rolls_back_without_delivery()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        using var cancellation = new CancellationTokenSource();
        var delivery = new RecordingDelivery(fixture) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pending = Service(delivery).CreateTextAsync(room.UserId, room.RoomId, "cancelled synthetic", cancellation.Token);
        await delivery.ReachedAvailability.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 실행
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // 검증
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await context.ChatMessages.CountAsync(value => value.ChatRoomId == room.RoomId));
        Assert.Empty(delivery.Attempted);
    }

    private ChatMessageService Service(RecordingDelivery delivery, SyntheticProfiles? profiles = null)
    {
        return new(new EfChatMessageTransaction(fixture.Contexts, profiles ?? new SyntheticProfiles(), delivery,
            NullLogger<EfChatMessageTransaction>.Instance), () => ChatMySqlFixture.Epoch.AddMinutes(1));
    }

    private async Task<ChatRoomMemberEntity> AddMemberAsync(SeededReadRoom room, long userId)
    {
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var member = new ChatRoomMemberEntity
        {
            ChatRoomId = room.RoomId,
            UserId = userId,
            Role = "MEMBER",
            JoinedAt = ChatMySqlFixture.Epoch,
            CreatedAt = ChatMySqlFixture.Epoch,
            UpdatedAt = ChatMySqlFixture.Epoch
        };
        context.ChatRoomMembers.Add(member);
        await context.SaveChangesAsync();
        return member;
    }

    // 계정/스토리지/전송은 명시적 합성 대역이다. MySQL은 소유권 manifest가 검증한 실제 서버다.
    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public bool Missing
        {
            get; init;
        }
        public TaskCompletionSource<bool>? AuditGate
        {
            get; init;
        }
        public TaskCompletionSource<bool> ReachedAudit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            return Missing ? throw new ChatMessageDependencyUnavailableException("synthetic profile")
                : Task.FromResult(new ChatUserMessageProfile(userId, "합성 사용자", "synthetic@example.invalid", null));
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            throw new ChatMessageDependencyUnavailableException("synthetic storage");
        }

        public async Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            ReachedAudit.TrySetResult(true);
            if (AuditGate is not null)
            {
                await AuditGate.Task.WaitAsync(cancellationToken);
            }
            return "synthetic@example.invalid";
        }
    }

    private sealed class SyntheticRoomAccount : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token)
        {
            throw new InvalidOperationException("Unused test port.");
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
        {
            return Task.FromResult("synthetic@example.invalid");
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token)
        {
            throw new InvalidOperationException("Unused test port.");
        }
    }

    private sealed class RecordingAiSettingsEvents : IOpenMembershipDelivery
    {
        public List<OpenMembershipIntent> Delivered { get; } = [];
        public Task ValidateAvailabilityAsync(IReadOnlyList<OpenMembershipIntent> intents, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task DeliverAsync(OpenMembershipIntent intent, CancellationToken token)
        {
            Delivered.Add(intent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDelivery(ChatMySqlFixture fixture) : IChatMessageEventDelivery
    {
        public bool Missing
        {
            get; init;
        }
        public bool FailFirst
        {
            get; init;
        }
        public TaskCompletionSource<bool>? Gate
        {
            get; init;
        }
        public TaskCompletionSource<bool> ReachedAvailability { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessageIntent> Attempted { get; } = [];
        public List<bool> ObservedCommittedStates { get; } = [];

        public async Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken cancellationToken)
        {
            ReachedAvailability.TrySetResult(true);
            if (Missing)
            {
                throw new ChatMessageDependencyUnavailableException("synthetic delivery");
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task DeliverAsync(ChatMessageIntent intent, CancellationToken cancellationToken)
        {
            Attempted.Add(intent);
            long id = intent switch
            {
                ChatMessageCreatedIntent created => created.Message.Id,
                ChatTranslationRequestedIntent translation => translation.MessageId,
                ChatHumanMessageRecordedIntent human => human.MessageId,
                ChatAiTriggerRequestedIntent ai => ai.MessageId,
                _ => throw new InvalidOperationException("Unknown test intent.")
            };
            await using var context = await fixture.Contexts.CreateDbContextAsync(cancellationToken);
            ObservedCommittedStates.Add(await context.ChatMessages.AnyAsync(message => message.Id == id, cancellationToken));
            if (FailFirst && Attempted.Count == 1)
            {
                throw new IOException("synthetic delivery failure");
            }
        }
    }
}
