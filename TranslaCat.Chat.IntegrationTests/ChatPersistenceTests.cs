using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatPersistenceTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Migration_ReapplicationPreservesHistoryAndSchemaHasNoExternalForeignKeys()
    {
        // 준비
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var before = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

        // 실행
        await context.Database.MigrateAsync();
        var after = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var tables = await QueryLongAsync("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'");
        var foreign = await QueryLongAsync("SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL AND (REFERENCED_TABLE_SCHEMA <> DATABASE() OR REFERENCED_TABLE_NAME NOT LIKE '%chat%')");
        var indexes = await QueryLongAsync("SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='chat_message' AND INDEX_NAME IN ('idx_chat_message_room_created_id','idx_chat_message_room_id_id','uk_chat_message_ai_request_id')");

        // 검증
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(14, tables);
        Assert.Equal(0, foreign);
        Assert.Equal(3, indexes);
    }

    [Fact]
    public async Task Crud_RoundTripsSignedExternalIdNullUnicodeBooleanAndMicrosecondTime()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(userId: long.MaxValue - 1);
        var timestamp = ChatMySqlFixture.Epoch.AddTicks(1234560);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
            member.UserId = long.MaxValue;
            member.ShowOriginal = false;
            member.LastReadAt = null;
            member.LastReadMessageId = 9007199254740993;
            member.UpdatedAt = timestamp;
            var message = await context.ChatMessages.SingleAsync(value => value.Id == room.FirstId);
            message.Content = "合成 한글 😀 e\u0301";
            await context.SaveChangesAsync();
        }

        // 실행 — 새 connection/context로 저장값을 읽는다.
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var actual = await read.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
        var text = await read.ChatMessages.SingleAsync(value => value.Id == room.FirstId);

        // 검증
        Assert.Equal(long.MaxValue, actual.UserId);
        Assert.False(actual.ShowOriginal);
        Assert.True(actual.ShowTranslation);
        Assert.Null(actual.LastReadAt);
        Assert.Equal(9007199254740993, actual.LastReadMessageId);
        Assert.Equal(timestamp, actual.UpdatedAt);
        Assert.Equal(DateTimeKind.Unspecified, actual.UpdatedAt.Kind);
        Assert.Equal("合成 한글 😀 e\u0301", text.Content);
    }

    [Fact]
    public async Task UniqueAndInternalForeignKeyConstraintsAreEnforcedByMySql()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using var duplicate = await fixture.Contexts.CreateDbContextAsync();
        duplicate.ChatRoomMembers.Add(new()
        {
            ChatRoomId = room.RoomId,
            UserId = room.UserId,
            Role = "MEMBER",
            JoinedAt = ChatMySqlFixture.Epoch,
            CreatedAt = ChatMySqlFixture.Epoch,
            UpdatedAt = ChatMySqlFixture.Epoch
        });

        // 실행 / 검증 — unique 위반과 존재하지 않는 내부 방 FK 위반을 분리한다.
        var unique = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Equal(1062, Assert.IsType<MySqlException>(unique.InnerException).Number);
        await using var missingRoom = await fixture.Contexts.CreateDbContextAsync();
        missingRoom.ChatMessages.Add(ChatMySqlFixture.Message(-991, 42, ChatMySqlFixture.Epoch));
        var foreign = await Assert.ThrowsAsync<DbUpdateException>(() => missingRoom.SaveChangesAsync());
        Assert.Equal(1452, Assert.IsType<MySqlException>(foreign.InnerException).Number);
    }

    [Fact]
    public async Task Read_AdvanceCommitsBeforeDeliveryAndNoOpPreservesTime()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        var delivered = new RecordingDelivery(fixture, room.MemberId);
        var now = ChatMySqlFixture.Epoch.AddMinutes(1);
        var service = new ChatRoomReadService(Transaction(delivered), () => now);

        // 실행
        var first = await service.MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));
        var noOp = await new ChatRoomReadService(Transaction(delivered), () => now.AddMinutes(2))
            .MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));

        // 검증 — 독립 DB 조회에서 commit 상태가 보여야 전달을 통과한다.
        Assert.Equal(now, first.LastReadAt);
        Assert.Equal(first, noOp);
        Assert.Equal(1, first.UnreadCount);
        Assert.Equal(2, delivered.Self.Count);
        var memberEvent = Assert.Single(delivered.Room);
        Assert.Null(memberEvent.PreviousLastReadMessageId);
        Assert.Equal(room.FirstId, memberEvent.LastReadMessageId);
        Assert.All(delivered.PersistedAtDelivery, cursor => Assert.Equal(room.FirstId, cursor));
    }

    [Fact]
    public async Task Read_NoOpKeepsNullableTimeAndStillValidatesTheTarget()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
            member.LastReadMessageId = room.SecondId;
            member.LastReadAt = null;
            await context.SaveChangesAsync();
        }
        var events = new RecordingDelivery(fixture, room.MemberId);
        var service = new ChatRoomReadService(Transaction(events), () => ChatMySqlFixture.Epoch.AddHours(1));

        // 실행
        var response = await service.MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));
        var error = await Assert.ThrowsAsync<ChatReadException>(() => service.MarkAsReadAsync(room.UserId, room.RoomId, new(-1)));

        // 검증
        Assert.Null(response.LastReadAt);
        Assert.Equal(room.SecondId, response.LastReadMessageId);
        Assert.Single(events.Self);
        Assert.Empty(events.Room);
        Assert.Equal("CHAT_ROOM_READ_MESSAGE_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task UnreadQuery_ExcludesOwnSystemDeletedAndBeforeJoinButIncludesAi()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var own = ChatMySqlFixture.Message(room.RoomId, room.UserId, ChatMySqlFixture.Epoch.AddSeconds(3));
            var system = ChatMySqlFixture.Message(room.RoomId, null, ChatMySqlFixture.Epoch.AddSeconds(3));
            system.MessageType = "SYSTEM";
            system.SenderType = "SYSTEM";
            var deleted = ChatMySqlFixture.Message(room.RoomId, 999, ChatMySqlFixture.Epoch.AddSeconds(3));
            deleted.DeletedAt = ChatMySqlFixture.Epoch.AddSeconds(4);
            var before = ChatMySqlFixture.Message(room.RoomId, 999, ChatMySqlFixture.Epoch.AddSeconds(-1));
            var ai = ChatMySqlFixture.Message(room.RoomId, null, ChatMySqlFixture.Epoch.AddSeconds(3));
            ai.SenderType = "AI";
            context.ChatMessages.AddRange(own, system, deleted, before, ai);
            await context.SaveChangesAsync();
        }

        // 실행
        var response = await new ChatRoomReadService(Transaction(new RecordingDelivery(fixture, room.MemberId)),
            () => ChatMySqlFixture.Epoch.AddMinutes(1)).MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));

        // 검증
        Assert.Equal(2, response.UnreadCount);
    }

    [Fact]
    public async Task SaveAndFlush_FailureBeforeOuterCommitRollsBackAndNeverDelivers()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        var events = new RecordingDelivery(fixture, room.MemberId);

        // 실행
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transaction(events).ExecuteAsync(async (session, token) =>
        {
            var member = (await session.FindActiveMemberForUpdateAsync(room.RoomId, room.UserId, token))!;
            await session.SaveAndFlushAsync(member, new(room.FirstId, ChatMySqlFixture.Epoch), token);
            session.RegisterAfterCommit(new ChatReadUpdated("synthetic@example.invalid", room.UserId,
                new(room.RoomId, room.FirstId, ChatMySqlFixture.Epoch, 0)));
            throw new InvalidOperationException("synthetic failure before commit");
        }, CancellationToken.None));

        // 검증
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Null((await read.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId)).LastReadMessageId);
        Assert.Empty(events.Self);
        Assert.Empty(events.Room);
    }

    [Fact]
    public async Task TwoIndependentTransactions_LockWaitPreventsAnOlderCursorFromOverwritingNewer()
    {
        // 준비 — 첫 transaction의 실제 행 lock을 잡은 뒤 두 번째 요청을 진입시킨다.
        var room = await fixture.SeedReadRoomAsync();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new RecordingDelivery(fixture, room.MemberId);
        var first = Transaction(events).ExecuteAsync(async (session, token) =>
        {
            var member = (await session.FindActiveMemberForUpdateAsync(room.RoomId, room.UserId, token))!;
            var advanced = member.Cursor.Advance(room.SecondId, ChatMySqlFixture.Epoch.AddMinutes(2));
            await session.SaveAndFlushAsync(member, advanced.Cursor, token);
            held.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return new(room.RoomId, advanced.Cursor.LastReadMessageId, advanced.Cursor.LastReadAt, 0);
        }, CancellationToken.None);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 실행 — 별도 context/connection 요청이 MySQL data_lock_waits에 나타난 후 첫 commit을 허용한다.
        var second = new ChatRoomReadService(Transaction(events), () => ChatMySqlFixture.Epoch.AddMinutes(3))
            .MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));
        try
        {
            await WaitForDatabaseLockAsync();
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);

        // 검증
        Assert.All(results, result => Assert.Equal(room.SecondId, result.LastReadMessageId));
        Assert.All(results, result => Assert.Equal(ChatMySqlFixture.Epoch.AddMinutes(2), result.LastReadAt));
        Assert.Single(events.Self);
        Assert.Empty(events.Room);
    }

    [Fact]
    public async Task MembershipRemoval_HoldingTheSameRowLockRejectsReadAfterCommit()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using var removal = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await removal.Database.BeginTransactionAsync();
        await removal.ChatRoomMembers.Where(member => member.Id == room.MemberId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(member => member.Active, false));
        var events = new RecordingDelivery(fixture, room.MemberId);

        // 실행
        var read = new ChatRoomReadService(Transaction(events), () => ChatMySqlFixture.Epoch)
            .MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));
        await WaitForDatabaseLockAsync();
        await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<ChatReadException>(() => read);

        // 검증
        Assert.Equal("CHAT_ROOM_MEMBER_ACCESS_DENIED", error.Code);
        Assert.Empty(events.Self);
    }

    [Fact]
    public async Task ConcurrentMessageCommit_IsNotIncludedInEarlierRepeatableReadSnapshot()
    {
        // 준비 — 원본의 REPEATABLE READ에서 첫 SELECT가 정한 snapshot의 범위를 검증한다.
        var room = await fixture.SeedReadRoomAsync();
        long? observed = null;

        // 실행
        await Transaction(new RecordingDelivery(fixture, room.MemberId)).ExecuteAsync(async (session, token) =>
        {
            await session.ValidateOpenRoomMemberAccessAsync(room.UserId, room.RoomId, token);
            await session.FindActiveMemberForUpdateAsync(room.RoomId, room.UserId, token);
            await using var writer = await fixture.Contexts.CreateDbContextAsync(token);
            writer.ChatMessages.Add(ChatMySqlFixture.Message(room.RoomId, 999, ChatMySqlFixture.Epoch.AddSeconds(3)));
            await writer.SaveChangesAsync(token);
            observed = await session.CountUnreadAsync(room.UserId, room.RoomId, token);
            return new(room.RoomId, null, null, observed.Value);
        }, CancellationToken.None);

        // 검증 — 다른 connection에서 commit한 세 번째 메시지는 다음 요청의 snapshot에서 보인다.
        Assert.Equal(2, observed);
        var response = await new ChatRoomReadService(Transaction(new RecordingDelivery(fixture, room.MemberId)),
            () => ChatMySqlFixture.Epoch).MarkAsReadAsync(room.UserId, room.RoomId, new(room.FirstId));
        Assert.Equal(2, response.UnreadCount);
    }

    private EfChatReadTransaction Transaction(IChatReadEventDelivery delivery)
    {
        return new(fixture.Contexts,
        new SyntheticRecipient(), delivery, NullLogger<EfChatReadTransaction>.Instance);
    }

    private async Task<long> QueryLongAsync(string sql)
    {
        await using var connection = new MySqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task WaitForDatabaseLockAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (await QueryLongAsync("SELECT COUNT(*) FROM performance_schema.data_lock_waits w JOIN performance_schema.data_locks l ON l.ENGINE_LOCK_ID=w.REQUESTING_ENGINE_LOCK_ID WHERE l.OBJECT_SCHEMA=DATABASE() AND l.OBJECT_NAME='chat_room_member'") == 0)
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class SyntheticRecipient : IChatReadRecipientResolver
    {
        public Task<string?> ResolveUsernameAsync(long userId, CancellationToken token)
        {
            return Task.FromResult<string?>("synthetic@example.invalid");
        }
    }

    private sealed class RecordingDelivery(ChatMySqlFixture fixture, long memberId) : IChatReadEventDelivery
    {
        public ConcurrentQueue<ChatReadUpdated> Self { get; } = new();
        public ConcurrentQueue<ChatMemberReadUpdated> Room { get; } = new();
        public ConcurrentQueue<long?> PersistedAtDelivery { get; } = new();

        public async Task DeliverAsync(ChatReadUpdated message, CancellationToken token)
        {
            await using var independent = await fixture.Contexts.CreateDbContextAsync(token);
            PersistedAtDelivery.Enqueue(await independent.ChatRoomMembers.Where(member => member.Id == memberId)
                .Select(member => member.LastReadMessageId).SingleAsync(token));
            Self.Enqueue(message);
        }

        public Task DeliverAsync(ChatMemberReadUpdated message, CancellationToken token)
        {
            Room.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
