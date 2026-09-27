using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.UnitTests.Read;

public sealed partial class ChatReadApplicationTests
{
    [Theory]
    [InlineData(null, true, null, "CHAT_ROOM_ID_REQUIRED")]
    [InlineData(8L, true, null, "CHAT_ROOM_LAST_READ_MESSAGE_ID_REQUIRED")]
    [InlineData(8L, false, null, "CHAT_ROOM_LAST_READ_MESSAGE_ID_REQUIRED")]
    public async Task Invalid_request_is_rejected_before_transaction_or_access(
        long? roomId, bool nullRequest, long? candidate, string expectedCode)
    {
        // 준비: 방 ID와 request가 모두 null이면 방 ID 실패가 먼저다.
        var transaction = new ControlledChatReadTransaction();
        var service = new ChatRoomReadService(transaction, () => ReadAt);
        var request = nullRequest ? null : new ChatRoomReadRequest(candidate);

        // 실행
        var error = await Assert.ThrowsAsync<ChatReadException>(
            () => service.MarkAsReadAsync(UserId, roomId, request));

        // 검증: Domain의 null 후보 no-op과 다르게 성공 응답/이벤트를 만들지 않는다.
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, transaction.ExecutionCount);
    }

    [Theory]
    [InlineData("OPEN_CHAT_BANNED")]
    [InlineData("OPEN_CHAT_MEMBER_ACCESS_DENIED")]
    [InlineData("OPEN_CHAT_ROOM_NOT_FOUND")]
    public async Task Access_collaborator_failure_precedes_membership_and_message_lookup(string sourceCode)
    {
        // 준비: 원본 collaborator의 판단 결과만 모사하며 ban/OPEN 조회 자체는 구현하지 않는다.
        var denial = new ChatReadException(sourceCode, "fixture access denial");
        var session = new ControlledChatReadSession(member: null, accessOutcome: () => throw denial);

        // 실행
        var error = await Assert.ThrowsAsync<ChatReadException>(() => ExecuteCommittedAsync(session, 100));

        // 검증
        Assert.Same(denial, error);
        Assert.Equal(["begin", "access", "rollback"], session.Trace);
        Assert.Empty(session.MemberRequests);
        Assert.Empty(session.MessageRequests);
        AssertRejectedWithoutSideEffects(session);
    }

    [Fact]
    public async Task Absent_active_membership_is_denied_before_missing_message()
    {
        // 준비: inactive/deleted/다른 room·user를 제외한 활성 조회의 부재 결과를 모사한다.
        var session = new ControlledChatReadSession(member: null, accessOutcome: () => { });

        // 실행
        var error = await Assert.ThrowsAsync<ChatReadException>(() => ExecuteCommittedAsync(session, 100));

        // 검증
        Assert.Equal("CHAT_ROOM_MEMBER_ACCESS_DENIED", error.Code);
        Assert.Equal(["begin", "access", "member", "rollback"], session.Trace);
        Assert.Empty(session.MessageRequests);
        AssertRejectedWithoutSideEffects(session);
    }

    [Theory]
    [InlineData("missing", 100L, "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("other-room", 40L, "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("deleted", 100L, "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("not-sent", 40L, "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    [InlineData("before-join", 100L, "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    [InlineData("null-created", 40L, "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    [InlineData("null-joined", 100L, "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    public async Task Equal_or_older_candidate_still_requires_valid_target_message(
        string invalidCondition, long candidate, string expectedCode)
    {
        // 준비
        var cursor = new ReadCursor(100, PreviousReadAt);
        var session = CreateSession(cursor);
        var message = Message(candidate);
        message = invalidCondition switch
        {
            "other-room" => message with { ChatRoomId = RoomId + 1 },
            "deleted" => message with { DeletedAt = ReadAt },
            "not-sent" => message with { IsSent = false },
            "before-join" => message with { CreatedAt = JoinedAt.AddTicks(-1) },
            "null-created" => message with { CreatedAt = null },
            _ => message
        };
        if (invalidCondition != "missing")
        {
            session.AddMessage(message);
        }
        if (invalidCondition == "null-joined")
        {
            session.Member = session.Member! with
            {
                JoinedAt = null
            };
        }

        // 실행
        var error = await Assert.ThrowsAsync<ChatReadException>(() => ExecuteCommittedAsync(session, candidate));

        // 검증
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(["begin", "access", "member", "message", "rollback"], session.Trace);
        Assert.Equal(cursor, session.CommittedCursor);
        AssertRejectedWithoutSideEffects(session);
    }

    [Theory]
    [InlineData(true, "CHAT_ROOM_READ_MESSAGE_NOT_FOUND", "읽음 처리할 메시지를 찾을 수 없습니다.")]
    [InlineData(false, "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE", "전송 완료된 메시지만 읽음 처리할 수 있습니다.")]
    public async Task Overlapping_message_failures_preserve_deleted_then_sent_then_join_order(
        bool deleted, string expectedCode, string expectedMessage)
    {
        // 준비: SENT와 가입 시점은 서로 다른 메시지지만 동일한 source 오류 code를 사용한다.
        var session = CreateSession(new ReadCursor(100, PreviousReadAt));
        session.AddMessage(Message(100) with
        {
            DeletedAt = deleted ? ReadAt : null,
            IsSent = false,
            CreatedAt = JoinedAt.AddTicks(-1)
        });

        // 실행
        var error = await Assert.ThrowsAsync<ChatReadException>(() => ExecuteCommittedAsync(session, 100));

        // 검증
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedMessage, error.Message);
        AssertRejectedWithoutSideEffects(session);
    }

    private static void AssertRejectedWithoutSideEffects(ControlledChatReadSession session)
    {
        Assert.Empty(session.Saves);
        Assert.Empty(session.CountRequests);
        Assert.Empty(session.Registered);
        Assert.Empty(session.DeliveryAttempts);
        Assert.Empty(session.Delivered);
        Assert.Equal(0, session.PendingCount);
        Assert.False(session.IsCommitted);
        Assert.True(session.IsRolledBack);
    }
}
