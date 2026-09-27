using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Ai;
using TranslaCat.Chat.Infrastructure.Persistence.AiManagement;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatAiPersistenceTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Mention_planner_uses_context_order_aliases_and_excludes_trigger_from_context()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);

        // 실행
        var plan = Assert.Single(await store.PlanAsync(seed.Room.SecondId, default));

        // 검증 — 실제 MySQL 조회 결과이며 사용자 이름만 명시적 합성 port이다.
        Assert.Equal("MENTION", plan.Request.TriggerType);
        Assert.Equal($"chat-ai:mention:{seed.Room.SecondId}:{seed.MemberId}", plan.Request.RequestId);
        Assert.Equal("member-2", plan.Request.TriggerMessage!.SenderId);
        Assert.Equal("synthetic-name-" + seed.Room.UserId, plan.Request.TriggerMessage.SenderName);
        var context = Assert.Single(plan.Request.ContextMessages);
        Assert.Equal(seed.Room.FirstId, context.MessageId);
        Assert.Equal("member-1", context.SenderId);
        Assert.Equal("ja", plan.Request.AiMember.OriginalLanguageCode);
    }

    [Fact]
    public async Task Denied_mention_does_not_fall_through_to_conversation()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            (await db.ChatRoomAiSettings.SingleAsync(row => row.ChatRoomId == seed.Room.RoomId)).MentionPermission = "OWNER_ADMIN_ONLY";
            (await db.ChatRoomMembers.SingleAsync(row => row.Id == seed.Room.MemberId)).Role = "MEMBER";
            await db.SaveChangesAsync();
        }

        // 실행
        var plans = await ChatAiTestData.Store(fixture, nextRandom: _ => throw new InvalidOperationException("Must not use conversation gate"))
            .PlanAsync(seed.Room.SecondId, default);

        // 검증
        Assert.Empty(plans);
    }

    [Fact]
    public async Task Mention_capacity_and_context_character_limits_use_current_settings()
    {
        // 준비 — 공유 DEFAULT 설정은 종료 시 원래 값으로 복구한다.
        await using var settings = await ChatAiTestData.SettingsAsync(fixture,
            value => value with { MentionRateLimitCount = 1, ContextMaxCharacters = 1 });
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            var first = await db.ChatMessages.SingleAsync(row => row.Id == seed.Room.FirstId);
            first.SenderUserId = seed.Room.UserId;
            first.Content = "@Mika previous";
            await db.SaveChangesAsync();
        }

        // 실행 / 검증 — 직전 같은 사용자의 멘션은 호출 상한을 소진한다.
        Assert.Empty(await ChatAiTestData.Store(fixture).PlanAsync(seed.Room.SecondId, default));
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            (await db.ChatMessages.SingleAsync(row => row.Id == seed.Room.FirstId)).Content = "long plain context";
            await db.SaveChangesAsync();
        }

        var plan = Assert.Single(await ChatAiTestData.Store(fixture).PlanAsync(seed.Room.SecondId, default));
        Assert.Empty(plan.Request.ContextMessages);
        Assert.Equal("member-1", plan.Request.TriggerMessage!.SenderId);
    }

    [Fact]
    public async Task Conversation_gates_select_only_one_candidate_after_two_human_messages()
    {
        // 준비
        await using var settings = await ChatAiTestData.SettingsAsync(fixture,
            value => value with { ConversationResponseRate = 100, ConversationMinHumanMessagesAfterAi = 2 });
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            (await db.ChatMessages.SingleAsync(row => row.Id == seed.Room.SecondId)).Content = "plain conversation";
            await db.SaveChangesAsync();
        }

        // 실행
        var plans = await ChatAiTestData.Store(fixture).PlanAsync(seed.Room.SecondId, default);

        // 검증
        Assert.Equal("CONVERSATION", Assert.Single(plans).Request.TriggerType);
    }

    [Fact]
    public async Task Reply_saves_once_with_pending_translation_and_delivers_only_after_commit()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var delivery = new ChatAiTestData.ObservingDelivery(fixture);
        var store = ChatAiTestData.Store(fixture, delivery);
        var plan = Assert.Single(await store.PlanAsync(seed.Room.SecondId, default));

        // 실행 — 서로 다른 DbContext의 동시 저장을 실제 room lock으로 직렬화한다.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.SaveReplyAsync(plan, "  合成応答  ", null, default)));

        // 검증
        Assert.Single(results, value => value == ChatAiProcessingResult.Responded);
        Assert.Equal(3, results.Count(value => value == ChatAiProcessingResult.Duplicate));
        Assert.True(delivery.ObservedCommitted);
        Assert.Collection(delivery.Events,
            value =>
            {
                var message = Assert.IsType<ChatMessageCreatedIntent>(value).Message;
                Assert.Equal("AI", message.SenderType);
                Assert.Null(message.SenderUserId);
                Assert.Equal(seed.MemberId, message.SenderAiMemberId);
                Assert.Equal("合成応答", message.Content);
                Assert.Equal(1, message.UnreadMemberCount);
                Assert.Equal("en", Assert.Single(message.Translations).LanguageCode);
            },
            value => Assert.Null(Assert.IsType<ChatTranslationRequestedIntent>(value).SenderUserId));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await db.ChatMessages.CountAsync(row => row.AiRequestId == plan.Request.RequestId));
    }

    [Fact]
    public async Task Dependency_validation_rolls_back_message_and_pending_translation()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var delivery = new ChatAiTestData.ObservingDelivery(fixture) { RejectValidation = true };
        var store = ChatAiTestData.Store(fixture, delivery);
        var plan = Assert.Single(await store.PlanAsync(seed.Room.SecondId, default));

        // 실행
        await Assert.ThrowsAsync<ChatMessageDependencyUnavailableException>(() => store.SaveReplyAsync(plan, "synthetic", null, default));

        // 검증
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatMessages.AnyAsync(row => row.AiRequestId == plan.Request.RequestId));
        Assert.Empty(delivery.Events);
    }

    [Fact]
    public async Task Committed_delivery_failure_does_not_remove_message_or_skip_translation_intent()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var delivery = new ChatAiTestData.ObservingDelivery(fixture) { FailFirstDelivery = true };
        var store = ChatAiTestData.Store(fixture, delivery);
        var plan = Assert.Single(await store.PlanAsync(seed.Room.SecondId, default));

        // 실행
        var result = await store.SaveReplyAsync(plan, "synthetic", null, default);

        // 검증 — 외부 전달은 합성 관찰 대역이며 실제 STOMP 검증은 별도 runtime test이다.
        Assert.Equal(ChatAiProcessingResult.Responded, result);
        Assert.Equal(2, delivery.Events.Count);
        Assert.IsType<ChatTranslationRequestedIntent>(delivery.Events[1]);
        Assert.True(delivery.ObservedCommitted);
    }

    [Fact]
    public async Task Open_close_transaction_blocks_reply_then_rejects_the_closed_room()
    {
        // 준비 — 두 DbContext의 OPEN 행 잠금 경합을 실제 DB에서 확인한다.
        var seed = await ChatAiTestData.SeedAsync(fixture, "OPEN");
        var store = ChatAiTestData.Store(fixture);
        var plan = Assert.Single(await store.PlanAsync(seed.Room.SecondId, default));
        await using var closing = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await closing.Database.BeginTransactionAsync();
        var rows = await closing.OpenChatRooms.FromSqlInterpolated(
            $"SELECT * FROM open_chat_room WHERE chat_room_id = {seed.Room.RoomId} FOR UPDATE").ToListAsync();
        rows.Single().Status = "CLOSED";
        await closing.SaveChangesAsync();

        // 실행
        var pending = store.SaveReplyAsync(plan, "must not persist", null, default);
        await WaitForDatabaseLockAsync();
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증
        Assert.Equal(ChatAiProcessingResult.Failed, result);
        Assert.Empty(await store.PlanAsync(seed.Room.SecondId, default));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatMessages.AnyAsync(row => row.AiRequestId == plan.Request.RequestId));
    }

    [Fact]
    public async Task Activity_reset_is_monotonic_and_concurrent_claim_has_one_owner_for_120_seconds()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);
        var recorded = new ChatHumanMessageRecordedIntent(seed.Room.SecondId, seed.Room.RoomId, ChatAiTestData.Now);
        await store.RecordHumanAsync(recorded, default);
        await store.RecordHumanAsync(recorded, default);
        await store.RecordHumanAsync(recorded with
        {
            MessageId = seed.Room.FirstId,
            CreatedAt = ChatAiTestData.Now.AddDays(1)
        }, default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        DateTime due = activity.NextRevivalAt!.Value;

        // 실행
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ClaimRevivalAsync(activity.Id, due, default)));

        // 검증
        var claim = Assert.Single(claims, value => value is not null)!;
        Assert.Equal(1, claim.CycleVersion);
        Assert.Equal(1, claim.AttemptNumber);
        Assert.True(due >= recorded.CreatedAt.AddHours(24));
        Assert.InRange(due.TimeOfDay, TimeSpan.FromHours(10), TimeSpan.FromHours(22).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(due.AddSeconds(120), (await ActivityAsync(seed.Room.RoomId)).ClaimExpiresAt);
        Assert.Null(await store.ClaimRevivalAsync(activity.Id, due.AddSeconds(119), default));
    }

    [Fact]
    public async Task New_human_reset_invalidates_late_revival_before_message_save_and_finish()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);
        await store.RecordHumanAsync(new(seed.Room.FirstId, seed.Room.RoomId, ChatAiTestData.Now), default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        DateTime due = activity.NextRevivalAt!.Value;
        var claim = (await store.ClaimRevivalAsync(activity.Id, due, default))!;
        var plan = (await store.PlanRevivalAsync(claim, default))!;
        await store.RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, due), default);

        // 실행
        var result = await store.SaveReplyAsync(plan, "late synthetic result", claim, default);
        await store.FinishRevivalAsync(claim, ChatAiProcessingResult.Responded, due, default);

        // 검증
        Assert.Equal(ChatAiProcessingResult.Failed, result);
        var current = await ActivityAsync(seed.Room.RoomId);
        Assert.Equal(2, current.RevivalCycleVersion);
        Assert.Equal(0, current.RevivalStage);
        Assert.Null(current.ClaimToken);
        Assert.Null(plan.Request.TriggerMessage);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatMessages.AnyAsync(row => row.AiRequestId == claim.RequestId));
    }

    [Fact]
    public async Task Failed_revival_retries_same_key_and_skipped_stages_stop_after_third_attempt()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);
        await store.RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, ChatAiTestData.Now), default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        DateTime firstDue = activity.NextRevivalAt!.Value;
        var first = (await store.ClaimRevivalAsync(activity.Id, firstDue, default))!;

        // 실행 / 검증 — FAILED는 같은 stage/key, 정상 SKIPPED는 72h/168h 다음 stage로 진행한다.
        await store.FinishRevivalAsync(first, ChatAiProcessingResult.Failed, firstDue, default);
        activity = await ActivityAsync(seed.Room.RoomId);
        Assert.Equal(0, activity.RevivalStage);
        Assert.True(activity.NextRevivalAt >= firstDue.AddMinutes(5));
        var retry = (await store.ClaimRevivalAsync(activity.Id, activity.NextRevivalAt!.Value, default))!;
        Assert.Equal(first.RequestId, retry.RequestId);
        DateTime now = activity.NextRevivalAt.Value;
        await store.FinishRevivalAsync(retry, ChatAiProcessingResult.Skipped, now, default);

        for (int stage = 2; stage <= 3; stage++)
        {
            activity = await ActivityAsync(seed.Room.RoomId);
            Assert.True(activity.NextRevivalAt >= now.AddHours(stage == 2 ? 72 : 168));
            now = activity.NextRevivalAt!.Value;
            var next = (await store.ClaimRevivalAsync(activity.Id, now, default))!;
            Assert.Equal(stage, next.AttemptNumber);
            await store.FinishRevivalAsync(next, ChatAiProcessingResult.Skipped, now, default);
        }

        activity = await ActivityAsync(seed.Room.RoomId);
        Assert.Equal(3, activity.RevivalStage);
        Assert.True(activity.RevivalStopped);
        Assert.Null(activity.NextRevivalAt);
    }

    [Fact]
    public async Task Deferred_revival_does_not_reclaim_before_provider_retry_after()
    {
        // 준비: 기존 Revival 실패 예약을 실제 Chat DB 행에 만든다.
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var initial = ChatAiTestData.Store(fixture);
        await initial.RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, ChatAiTestData.Now), default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        DateTime due = activity.NextRevivalAt!.Value;
        var store = ChatAiTestData.Store(fixture, clock: () => due);
        var claim = (await store.ClaimRevivalAsync(activity.Id, due, default))!;

        // 실행: 원래 5분 실패 예약보다 긴 600초 Provider 대기를 저장한다.
        await store.FinishRevivalAsync(
            claim, ChatAiProcessingResult.Failed, due, default, TimeSpan.FromSeconds(600));

        // 검증: 같은 request ID는 유지하고 조기 claim은 거절된다.
        activity = await ActivityAsync(seed.Room.RoomId);
        Assert.True(activity.NextRevivalAt >= due.AddSeconds(600));
        Assert.Null(await store.ClaimRevivalAsync(activity.Id, due.AddSeconds(599), default));
        // 대기 만료가 Revival 허용 시간(10~22시) 밖일 수 있으므로 다음날 정오에 재개한다.
        DateTime nextAllowed = activity.NextRevivalAt!.Value.Date.AddDays(1).AddHours(12);
        var retry = await store.ClaimRevivalAsync(activity.Id, nextAllowed, default);
        Assert.Equal(claim.RequestId, retry!.RequestId);
    }

    private async Task<ChatRoomAiActivityEntity> ActivityAsync(long roomId)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        return await db.ChatRoomAiActivities.AsNoTracking().SingleAsync(row => row.ChatRoomId == roomId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_planner_or_claim_observes_settings_committed_while_room_lock_was_held(bool revival)
    {
        // 준비 — 잠금 이전 nonlocking 조회가 오래된 REPEATABLE READ snapshot을 만들면 실패하는 경합이다.
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);
        await store.RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, ChatAiTestData.Now), default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        await using var changing = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await changing.Database.BeginTransactionAsync();
        await changing.ChatRooms.FromSqlInterpolated(
            $"SELECT * FROM chat_room WHERE id = {seed.Room.RoomId} FOR UPDATE").ToListAsync();
        var setting = await changing.ChatRoomAiSettings.SingleAsync(row => row.ChatRoomId == seed.Room.RoomId);
        setting.RevivalEnabled = false;
        setting.MentionPermission = "OWNER_ADMIN_ONLY";
        (await changing.ChatRoomMembers.SingleAsync(row => row.Id == seed.Room.MemberId)).Role = "MEMBER";
        await changing.SaveChangesAsync();

        // 실행
        Task<object?> pending = revival
            ? ClaimAsync()
            : PlanAsync();
        await WaitForDatabaseLockAsync();
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증
        Assert.Null(result);

        async Task<object?> ClaimAsync()
        {
            return await store.ClaimRevivalAsync(activity.Id, activity.NextRevivalAt!.Value, default);
        }

        async Task<object?> PlanAsync()
        {
            return (await store.PlanAsync(seed.Room.SecondId, default)).FirstOrDefault();
        }
    }

    [Fact]
    public async Task Slow_external_name_lookup_does_not_hold_global_settings_lock_for_other_rooms()
    {
        // 준비 — 정상 DEFAULT 행을 먼저 만들고 서로 다른 방의 planner를 실제 transaction으로 실행한다.
        var first = await ChatAiTestData.SeedAsync(fixture);
        var second = await ChatAiTestData.SeedAsync(fixture);
        await ChatAiTestData.Store(fixture).ReadSettingsAsync(default);
        var names = new BlockingNames();
        var blocking = new EfChatAiStore(fixture.Contexts, new(), new ChatAiTestData.ObservingDelivery(fixture),
            () => ChatAiTestData.Now, _ => 0, NullLogger<EfChatAiStore>.Instance, names);
        var pending = blocking.PlanAsync(first.Room.SecondId, default);
        await names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // 실행 — 첫 방의 외부 이름 조회를 해제하지 않은 상태에서도 두 번째 방이 계획을 마쳐야 한다.
            var plans = await ChatAiTestData.Store(fixture).PlanAsync(second.Room.SecondId, default)
                .WaitAsync(TimeSpan.FromSeconds(3));

            // 검증
            Assert.Single(plans);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            names.Release.TrySetResult();
            await pending;
        }
    }

    [Fact]
    public async Task Expired_revival_claim_keeps_request_key_but_rejects_stale_token_at_reply_commit()
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        var store = ChatAiTestData.Store(fixture);
        await store.RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, ChatAiTestData.Now), default);
        var activity = await ActivityAsync(seed.Room.RoomId);
        DateTime due = activity.NextRevivalAt!.Value;
        var old = (await store.ClaimRevivalAsync(activity.Id, due, default))!;
        var plan = (await store.PlanRevivalAsync(old, default))!;

        // 실행 — 원본 120초 만료 뒤 재소유한 token만 결과를 저장할 수 있다.
        var current = (await store.ClaimRevivalAsync(activity.Id, due.AddSeconds(120), default))!;
        var staleResult = await store.SaveReplyAsync(plan, "stale", old, default);
        var currentResult = await store.SaveReplyAsync(plan, "current", current, default);

        // 검증
        Assert.NotEqual(old.ClaimToken, current.ClaimToken);
        Assert.Equal(old.RequestId, current.RequestId);
        Assert.Equal(ChatAiProcessingResult.Failed, staleResult);
        Assert.Equal(ChatAiProcessingResult.Responded, currentResult);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("current", (await db.ChatMessages.SingleAsync(row => row.AiRequestId == current.RequestId)).Content);
    }

    private async Task WaitForDatabaseLockAsync()
    {
        await using var observer = await fixture.Contexts.CreateDbContextAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await observer.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM performance_schema.data_lock_waits")
            .SingleAsync(deadline.Token) == 0)
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class BlockingNames : IChatAiUserNameReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> GetUserNameAsync(long userId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "synthetic";
        }
    }
}

internal static class ChatAiTestData
{
    internal static readonly DateTime Now = ChatMySqlFixture.Epoch.AddMinutes(20);

    internal static EfChatAiStore Store(ChatMySqlFixture fixture, IChatMessageEventDelivery? delivery = null,
        Func<int, int>? nextRandom = null, Func<DateTime>? clock = null)
    {
        return new(fixture.Contexts, new(), delivery ?? new ObservingDelivery(fixture),
        clock ?? (() => Now), nextRandom ?? (_ => 0), NullLogger<EfChatAiStore>.Instance, new Names());
    }

    internal static async Task<(SeededReadRoom Room, long MemberId)> SeedAsync(ChatMySqlFixture fixture, string roomType = "GROUP")
    {
        var room = await fixture.SeedReadRoomAsync(roomType);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var agent = new ChatAiAgentEntity
        {
            Nickname = "Mika",
            OriginalLanguageCode = "ja",
            PersonaPrompt = "synthetic persona",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.ChatAiAgents.Add(agent);
        await db.SaveChangesAsync();
        var member = new ChatRoomAiMemberEntity
        {
            ChatRoomId = room.RoomId,
            AiAgentId = agent.Id,
            JoinedAt = ChatMySqlFixture.Epoch,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.ChatRoomAiMembers.Add(member);
        db.ChatRoomAiSettings.Add(new ChatRoomAiSettingEntity
        {
            ChatRoomId = room.RoomId,
            DisclosureType = "PUBLIC",
            MentionPermission = "ALL_MEMBERS",
            ConversationEnabled = true,
            RevivalEnabled = true,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        var trigger = await db.ChatMessages.SingleAsync(row => row.Id == room.SecondId);
        trigger.Content = "@Mika synthetic request";
        trigger.SenderUserId = room.UserId;
        (await db.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).TranslationLanguageCode = "en";
        if (roomType == "OPEN")
        {
            db.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Status = "ACTIVE",
                Visibility = "PUBLIC",
                MaxMemberCount = 100,
                CreatedAt = Now,
                UpdatedAt = Now
            });
        }

        await db.SaveChangesAsync();
        return (room, member.Id);
    }

    internal static async Task<IAsyncDisposable> SettingsAsync(ChatMySqlFixture fixture,
        Func<ChatAiSystemSettings, ChatAiSystemSettings> change)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var row = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, Now, "synthetic", default, forUpdate: true);
        var snapshot = EfChatAiSystemSettings.Map(row);
        EfChatAiSystemSettings.Apply(row, change(snapshot));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new SettingsRestore(fixture, snapshot);
    }

    private sealed class SettingsRestore(ChatMySqlFixture fixture, ChatAiSystemSettings snapshot) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var db = await fixture.Contexts.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var row = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, Now, "synthetic", default, forUpdate: true);
            EfChatAiSystemSettings.Apply(row, snapshot);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
    }

    internal sealed class Names : IChatAiUserNameReader
    {
        public Task<string?> GetUserNameAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>("synthetic-name-" + userId);
        }
    }

    internal sealed class ObservingDelivery(ChatMySqlFixture fixture) : IChatMessageEventDelivery
    {
        public bool RejectValidation
        {
            get; init;
        }
        public bool FailFirstDelivery
        {
            get; init;
        }
        public bool ObservedCommitted
        {
            get; private set;
        }
        public List<ChatMessageIntent> Events { get; } = [];

        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken cancellationToken)
        {
            if (RejectValidation)
            {
                throw new ChatMessageDependencyUnavailableException("synthetic missing translation");
            }

            return Task.CompletedTask;
        }

        public async Task DeliverAsync(ChatMessageIntent intent, CancellationToken cancellationToken)
        {
            Events.Add(intent);
            if (intent is ChatMessageCreatedIntent created)
            {
                await using var db = await fixture.Contexts.CreateDbContextAsync(cancellationToken);
                ObservedCommitted = await db.ChatMessages.AnyAsync(row => row.Id == created.Message.Id, cancellationToken);
                if (FailFirstDelivery)
                {
                    throw new InvalidOperationException("synthetic delivery failure");
                }
            }
        }
    }
}
