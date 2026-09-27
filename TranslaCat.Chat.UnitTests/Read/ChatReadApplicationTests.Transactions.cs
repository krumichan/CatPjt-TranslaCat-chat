using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.UnitTests.Read;

public sealed partial class ChatReadApplicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_completion_and_flush_do_not_deliver_or_return_success_before_commit(bool noOp)
    {
        // 준비
        var prior = new ReadCursor(noOp ? 100 : 40, PreviousReadAt);
        var session = CreateSession(prior);
        session.AddMessage(Message(100));
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행: callback는 완료하되 commit 결정은 보류한다.
        var operation = service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(100));
        await session.WorkCompleted;

        // 검증: 저장/등록과 외부 전달·commit·응답 완료는 서로 다른 단계다.
        Assert.False(operation.IsCompleted);
        Assert.False(session.IsCommitted);
        Assert.Equal(prior, session.CommittedCursor);
        Assert.Equal(noOp ? 0 : 1, session.Saves.Count);
        Assert.Equal(noOp ? 1 : 2, session.Registered.Count);
        Assert.Equal(session.Registered.Count, session.PendingCount);
        Assert.Empty(session.DeliveryAttempts);
        Assert.Empty(session.Delivered);

        // 실행 / 검증: 명시적인 commit 성공 뒤에만 전달 담당으로 인계한다.
        session.AllowCommit();
        var response = await operation;
        Assert.True(session.IsCommitted);
        Assert.False(session.IsRolledBack);
        Assert.Equal(session.Registered.Count, session.Delivered.Count);
        Assert.Equal(100, response.LastReadMessageId);
        Assert.Equal(0, session.PendingCount);
        if (!noOp)
        {
            Assert.Equal(
                ["begin", "access", "member", "message", "save-flush", "count", "register-self", "register-room", "work-completed", "commit", "deliver-self", "deliver-room"],
                session.Trace);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Save_or_count_failure_rolls_back_without_success_events(bool failSave)
    {
        // 준비
        var prior = new ReadCursor(40, PreviousReadAt);
        var session = CreateSession(prior);
        session.AddMessage(Message(100));
        var failure = new InvalidOperationException("fixture storage failure");
        if (failSave)
        {
            session.SaveFailure = failure;
        }
        else
        {
            session.CountFailure = failure;
        }

        // 실행
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteCommittedAsync(session, 100));

        // 검증
        Assert.Same(failure, error);
        Assert.Single(session.Saves);
        Assert.Equal(failSave ? 0 : 1, session.CountRequests.Count);
        Assert.Equal(prior, session.CommittedCursor);
        Assert.True(session.IsRolledBack);
        Assert.False(session.IsCommitted);
        Assert.Empty(session.Registered);
        Assert.Empty(session.Delivered);
        Assert.Equal(0, session.PendingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_commit_or_explicit_rollback_discards_already_registered_intents(bool explicitRollback)
    {
        // 준비
        var prior = new ReadCursor(40, PreviousReadAt);
        var session = CreateSession(prior);
        session.AddMessage(Message(100));
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행: 성공 callback 이후 commit 결과만 실패로 전환한다.
        var operation = service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(100));
        await session.WorkCompleted;
        Assert.Equal(2, session.PendingCount);
        if (explicitRollback)
        {
            session.RequestRollback();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            var failure = new InvalidOperationException("fixture commit failure");
            session.FailCommit(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
        }

        // 검증: 등록 이력은 남지만 pending/전달/commit된 상태는 남지 않는다.
        Assert.Equal(2, session.Registered.Count);
        Assert.Equal(0, session.PendingCount);
        Assert.Equal(prior, session.CommittedCursor);
        Assert.True(session.IsRolledBack);
        Assert.False(session.IsCommitted);
        Assert.Empty(session.DeliveryAttempts);
        Assert.Empty(session.Delivered);
    }

    [Fact]
    public async Task Publisher_failure_keeps_commit_and_attempts_remaining_independent_intent()
    {
        // 준비
        var session = CreateSession(new ReadCursor(40, PreviousReadAt));
        session.AddMessage(Message(100));
        var failure = new InvalidOperationException("fixture self publisher failure");
        session.DeliveryFailure = intent => intent is ChatReadUpdated ? failure : null;

        // 실행
        var response = await ExecuteCommittedAsync(session, 100);

        // 검증: 재시도나 durable delivery를 주장하지 않고 전달 담당의 실패 기록만 검사한다.
        Assert.True(session.IsCommitted);
        Assert.False(session.IsRolledBack);
        Assert.Equal(new ReadCursor(100, ReadAt), session.CommittedCursor);
        Assert.Equal(100, response.LastReadMessageId);
        Assert.Equal(2, session.DeliveryAttempts.Count);
        Assert.Same(failure, Assert.Single(session.DeliveryFailures));
        Assert.IsType<ChatMemberReadUpdated>(Assert.Single(session.Delivered));
        Assert.Equal(
            ["commit", "deliver-self", "delivery-failed-self", "deliver-room"],
            session.Trace.TakeLast(4));
    }

    [Fact]
    public async Task Rolled_back_pending_events_do_not_leak_into_next_room_and_user_transaction()
    {
        // 준비
        var first = CreateSession();
        first.AddMessage(Message(15));
        var second = CreateSession();
        second.Member = second.Member! with
        {
            ChatRoomId = 99,
            UserId = 77,
            DestinationUsername = "next@example.test"
        };
        second.AddMessage(Message(25) with
        {
            ChatRoomId = 99
        });
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(first);
        transaction.Enqueue(second);
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행: 첫 요청의 등록 의도를 rollback한 뒤 다른 요청을 성공시킨다.
        var failed = service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(15));
        await first.WorkCompleted;
        first.RequestRollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed);
        second.AllowCommit();
        var response = await service.MarkAsReadAsync(77, 99, new ChatRoomReadRequest(25));

        // 검증
        Assert.Equal(2, transaction.ExecutionCount);
        Assert.Empty(first.Delivered);
        Assert.Equal(0, first.PendingCount);
        Assert.Equal(2, second.Delivered.Count);
        var self = Assert.IsType<ChatReadUpdated>(second.Delivered[0]);
        Assert.Equal("next@example.test", self.DestinationUsername);
        Assert.Equal(77, self.UserId);
        Assert.Equal(99, self.Response.ChatRoomId);
        var room = Assert.IsType<ChatMemberReadUpdated>(second.Delivered[1]);
        Assert.Equal(99, room.ChatRoomId);
        Assert.Equal(77, room.ReaderUserId);
        Assert.Equal(25, room.LastReadMessageId);
        Assert.Equal(99, response.ChatRoomId);
    }

    [Fact]
    public async Task Repeated_and_reverse_requests_keep_each_transactions_result_and_event_branch()
    {
        // 준비: 다음 snapshot에 직전 commit 값을 넘기며 fake에서 cursor 정책을 계산하지 않는다.
        var transaction = new ControlledChatReadTransaction();
        var service = new ChatRoomReadService(transaction, () => ReadAt);
        long[] candidates = [15, 15, 5, 30];
        long[] expectedIds = [15, 15, 15, 30];
        int[] expectedSaves = [1, 0, 0, 1];
        long?[] expectedPrevious = [null, null, null, 15];
        var committedCursor = new ReadCursor(null, null);

        for (var index = 0; index < candidates.Length; index++)
        {
            var session = CreateSession(committedCursor);
            session.AddMessage(Message(candidates[index]));
            transaction.Enqueue(session);
            session.AllowCommit();

            // 실행
            var response = await service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(candidates[index]));

            // 검증: 순차 요청 검증이며 DB 경쟁/lock 검증이 아니다.
            Assert.Equal(expectedIds[index], response.LastReadMessageId);
            Assert.Equal(expectedSaves[index], session.Saves.Count);
            Assert.Equal(1 + expectedSaves[index], session.Delivered.Count);
            if (expectedSaves[index] == 1)
            {
                var room = Assert.IsType<ChatMemberReadUpdated>(session.Delivered[1]);
                Assert.Equal(expectedPrevious[index], room.PreviousLastReadMessageId);
                Assert.Equal(expectedIds[index], room.LastReadMessageId);
            }

            committedCursor = session.CommittedCursor!.Value;
        }

        Assert.Equal(4, transaction.ExecutionCount);
    }

    [Fact]
    public async Task Registered_snapshots_do_not_reread_mutated_fixture_request_or_clock_after_commit()
    {
        // 준비
        var session = CreateSession(new ReadCursor(40, PreviousReadAt), ChatReadRoomType.Open);
        session.AddMessage(Message(100));
        session.UnreadCount = 7;
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        var clock = ReadAt;
        var service = new ChatRoomReadService(transaction, () => clock);
        var request = new ChatRoomReadRequest(100);

        // 실행: 등록 후 원본처럼 보일 수 있는 대역 상태와 외부 시각을 바꾼다.
        var operation = service.MarkAsReadAsync(UserId, RoomId, request);
        await session.WorkCompleted;
        request = request with
        {
            LastReadMessageId = 200
        };
        session.Member = session.Member! with
        {
            MemberId = 999,
            ChatRoomId = 999,
            UserId = 999,
            DestinationUsername = "changed@example.test",
            Cursor = new ReadCursor(200, ReadAt.AddDays(1))
        };
        session.UnreadCount = 99;
        clock = ReadAt.AddDays(2);
        session.AllowCommit();
        var response = await operation;

        // 검증: 발생 시각은 후속 transport DTO 책임이며 기존 read 시각을 재작성하지 않는다.
        Assert.Equal(200, request.LastReadMessageId);
        Assert.Equal(new ChatRoomReadResponse(RoomId, 100, ReadAt, 7), response);
        Assert.Equal(new ChatReadUpdated(Recipient, UserId, response), Assert.IsType<ChatReadUpdated>(session.Delivered[0]));
        Assert.Equal(
            new ChatMemberReadUpdated(RoomId, null, MemberId, 40, 100, ReadAt),
            Assert.IsType<ChatMemberReadUpdated>(session.Delivered[1]));
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_start_transaction()
    {
        // 준비
        using var source = new CancellationTokenSource();
        source.Cancel();
        var transaction = new ControlledChatReadTransaction();
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(100), source.Token));

        // 검증
        Assert.Equal(0, transaction.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_commit_discards_staged_state_and_pending_intents(bool cancelAfterCount)
    {
        // 준비
        using var source = new CancellationTokenSource();
        var prior = new ReadCursor(40, PreviousReadAt);
        var session = CreateSession(prior);
        session.AddMessage(Message(100));
        if (cancelAfterCount)
        {
            session.AfterCount = source.Cancel;
        }
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행: I/O 반환 직후와 callback 완료 후 commit 대기 중 취소를 각각 제어한다.
        var operation = service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(100), source.Token);
        await session.WorkCompleted;
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        // 검증
        Assert.All(session.Tokens, token => Assert.Equal(source.Token, token));
        Assert.Equal(cancelAfterCount ? 0 : 2, session.Registered.Count);
        Assert.Equal(prior, session.CommittedCursor);
        Assert.True(session.IsRolledBack);
        Assert.False(session.IsCommitted);
        Assert.Equal(0, session.PendingCount);
        Assert.Empty(session.Delivered);
    }

    [Fact]
    public async Task Cancellation_after_commit_does_not_reclassify_committed_read_as_rollback()
    {
        // 준비
        using var source = new CancellationTokenSource();
        var session = CreateSession();
        session.AddMessage(Message(100));
        session.DeliveryFailure = _ =>
        {
            source.Cancel();
            return null;
        };
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        session.AllowCommit();
        var service = new ChatRoomReadService(transaction, () => ReadAt);

        // 실행: commit 이후 전달 담당에 인계하는 동안 호출자가 취소한 상황이다.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(100), source.Token));

        // 검증: 실제 adapter는 commit 결과와 취소 결과를 구분해야 한다.
        Assert.True(source.IsCancellationRequested);
        Assert.True(session.IsCommitted);
        Assert.False(session.IsRolledBack);
        Assert.Equal(new ReadCursor(100, ReadAt), session.CommittedCursor);
        Assert.Equal(2, session.Delivered.Count);
    }
}
