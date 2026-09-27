using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.UnitTests.Read;

public sealed partial class ChatReadApplicationTests
{
    private const long RoomId = 8;
    private const long UserId = 42;
    private const long MemberId = 71;
    private const string Recipient = "reader@example.test";
    private static readonly DateTime JoinedAt = new(2026, 9, 20, 12, 0, 0);
    private static readonly DateTime PreviousReadAt = new(2026, 9, 21, 13, 0, 0);
    private static readonly DateTime ReadAt = new(2026, 9, 22, 14, 0, 0);

    [Fact]
    public async Task First_cursor_saves_once_and_registers_result_and_nullable_previous_snapshot()
    {
        // 준비
        var session = CreateSession();
        session.AddMessage(Message(15));
        session.UnreadCount = 6;

        // 실행
        var response = await ExecuteCommittedAsync(session, 15);

        // 검증
        Assert.Equal(new ChatRoomReadResponse(RoomId, 15, ReadAt, 6), response);
        var save = Assert.Single(session.Saves);
        Assert.Equal(new ReadCursor(15, ReadAt), save.Cursor);
        Assert.Equal(save.Cursor, session.CommittedCursor);
        Assert.Equal(new ChatReadUpdated(Recipient, UserId, response), Assert.IsType<ChatReadUpdated>(session.Registered[0]));
        Assert.Equal(
            new ChatMemberReadUpdated(RoomId, UserId, null, null, 15, ReadAt),
            Assert.IsType<ChatMemberReadUpdated>(session.Registered[1]));
        Assert.Equal(2, session.Registered.Count);
        Assert.Equal(2, session.Delivered.Count);
        Assert.Equal([(UserId, RoomId)], session.AccessRequests);
        Assert.Equal([(RoomId, UserId)], session.MemberRequests);
        Assert.Equal([(RoomId, 15L)], session.MessageRequests);
        Assert.Equal([(UserId, RoomId)], session.CountRequests);
    }

    [Fact]
    public async Task Jump_uses_only_target_message_and_exact_injected_time_even_when_earlier()
    {
        // 준비
        var session = CreateSession(new ReadCursor(40, ReadAt));
        session.AddMessage(Message(900));
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        session.AllowCommit();
        var service = new ChatRoomReadService(transaction, () => PreviousReadAt);

        // 실행
        var response = await service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(900));

        // 검증
        Assert.Equal(900, response.LastReadMessageId);
        Assert.Equal(PreviousReadAt, response.LastReadAt);
        Assert.Equal([(RoomId, 900L)], session.MessageRequests);
        Assert.Equal(new ReadCursor(900, PreviousReadAt), Assert.Single(session.Saves).Cursor);
        Assert.Equal(
            new ChatMemberReadUpdated(RoomId, UserId, null, 40, 900, PreviousReadAt),
            Assert.IsType<ChatMemberReadUpdated>(session.Registered[1]));
    }

    [Theory]
    [InlineData(100L, false)]
    [InlineData(40L, false)]
    [InlineData(100L, true)]
    [InlineData(40L, true)]
    public async Task Equal_or_older_candidate_preserves_nullable_state_and_emits_only_self(long candidate, bool nullReadAt)
    {
        // 준비
        DateTime? previousTime = nullReadAt ? null : PreviousReadAt;
        var session = CreateSession(new ReadCursor(100, previousTime));
        session.AddMessage(Message(candidate));
        session.UnreadCount = 13;

        // 실행
        var response = await ExecuteCommittedAsync(session, candidate);

        // 검증
        Assert.Empty(session.Saves);
        Assert.Equal(new ChatRoomReadResponse(RoomId, 100, previousTime, 13), response);
        Assert.Equal(new ReadCursor(100, previousTime), session.CommittedCursor);
        Assert.Single(session.CountRequests);
        var self = Assert.IsType<ChatReadUpdated>(Assert.Single(session.Registered));
        Assert.Equal(new ChatReadUpdated(Recipient, UserId, response), self);
        Assert.Same(self, Assert.Single(session.Delivered));
        Assert.Equal(
            ["begin", "access", "member", "message", "count", "register-self", "work-completed", "commit", "deliver-self"],
            session.Trace);
    }

    [Theory]
    [InlineData(ChatReadRoomType.Direct)]
    [InlineData(ChatReadRoomType.Group)]
    [InlineData(ChatReadRoomType.Open)]
    public async Task Public_identity_uses_room_type_while_private_event_retains_recipient_and_login_user(ChatReadRoomType roomType)
    {
        // 준비
        var session = CreateSession(roomType: roomType);
        session.AddMessage(Message(15));

        // 실행
        await ExecuteCommittedAsync(session, 15);

        // 검증
        var self = Assert.IsType<ChatReadUpdated>(session.Registered[0]);
        Assert.Equal(Recipient, self.DestinationUsername);
        Assert.Equal(UserId, self.UserId);
        var room = Assert.IsType<ChatMemberReadUpdated>(session.Registered[1]);
        Assert.Equal(roomType == ChatReadRoomType.Open ? null : UserId, room.ReaderUserId);
        Assert.Equal(roomType == ChatReadRoomType.Open ? MemberId : null, room.ReaderOpenChatMemberId);
    }

    [Theory]
    [InlineData(ChatReadRoomType.Direct)]
    [InlineData(ChatReadRoomType.Group)]
    [InlineData(ChatReadRoomType.Open)]
    public async Task Missing_selected_public_identity_does_not_fallback_to_other_identity(ChatReadRoomType roomType)
    {
        // 준비: 실제 저장소의 정상 행 조건과 별도로 원본 event 생성 guard를 검증한다.
        var session = CreateSession(roomType: roomType);
        session.Member = roomType == ChatReadRoomType.Open
            ? session.Member! with
            {
                MemberId = null
            }
            : session.Member! with
            {
                UserId = null
            };
        session.AddMessage(Message(15));

        // 실행
        await ExecuteCommittedAsync(session, 15);

        // 검증
        Assert.Single(session.Saves);
        Assert.IsType<ChatReadUpdated>(Assert.Single(session.Registered));
        Assert.IsType<ChatReadUpdated>(Assert.Single(session.Delivered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_private_destination_keeps_intent_without_replacing_it_with_user_id(string? destination)
    {
        // 준비: 실제 전송 skip은 미구현 publisher adapter 책임이다.
        var session = CreateSession();
        session.Member = session.Member! with
        {
            DestinationUsername = destination
        };
        session.AddMessage(Message(15));

        // 실행
        await ExecuteCommittedAsync(session, 15);

        // 검증
        var self = Assert.IsType<ChatReadUpdated>(session.Registered[0]);
        Assert.Equal(destination, self.DestinationUsername);
        Assert.Equal(UserId, self.UserId);
        Assert.Equal(2, session.Registered.Count);
    }

    [Theory]
    [InlineData(9007199254740992L, 9007199254740993L)]
    [InlineData(long.MaxValue - 1, long.MaxValue)]
    public async Task Large_ids_keep_exact_previous_and_new_values(long previous, long candidate)
    {
        // 준비
        var session = CreateSession(new ReadCursor(previous, PreviousReadAt));
        session.AddMessage(Message(candidate));
        session.UnreadCount = (long)int.MaxValue + 8;

        // 실행
        var response = await ExecuteCommittedAsync(session, candidate);

        // 검증
        Assert.Equal(candidate, response.LastReadMessageId);
        Assert.Equal((long)int.MaxValue + 8, response.UnreadCount);
        var room = Assert.IsType<ChatMemberReadUpdated>(session.Registered[1]);
        Assert.Equal(previous, room.PreviousLastReadMessageId);
        Assert.Equal(candidate, room.LastReadMessageId);
        Assert.Equal(candidate, Assert.Single(session.Saves).Cursor.LastReadMessageId);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Application_does_not_add_http_positive_id_validation(long candidate)
    {
        // 준비: 해당 ID의 유효한 조회 결과를 지정해 HTTP 전용 제한과 구분한다.
        var session = CreateSession();
        session.AddMessage(Message(candidate));

        // 실행
        var response = await ExecuteCommittedAsync(session, candidate);

        // 검증
        Assert.Equal(candidate, response.LastReadMessageId);
        Assert.Single(session.Saves);
        Assert.Equal([(RoomId, candidate)], session.MessageRequests);
    }

    [Fact]
    public async Task Message_at_exact_join_time_is_readable()
    {
        // 준비
        var session = CreateSession();
        session.AddMessage(Message(15) with
        {
            CreatedAt = JoinedAt
        });

        // 실행
        var response = await ExecuteCommittedAsync(session, 15);

        // 검증
        Assert.Equal(15, response.LastReadMessageId);
        Assert.Single(session.Saves);
    }

    private static ControlledChatReadSession CreateSession(
        ReadCursor? cursor = null,
        ChatReadRoomType roomType = ChatReadRoomType.Direct)
    {
        var member = new ChatReadMember(
            MemberId, RoomId, UserId, Recipient, roomType, JoinedAt, cursor ?? new ReadCursor(null, null));

        // fixture가 접근 성공을 명시한다. 실제 OPEN 접근 규칙을 이 fake가 검증하지 않는다.
        return new ControlledChatReadSession(member, accessOutcome: () => { });
    }

    private static ChatReadMessage Message(long id)
    {
        return new(id, RoomId, null, true, JoinedAt.AddMinutes(1));
    }

    private static Task<ChatRoomReadResponse> ExecuteCommittedAsync(ControlledChatReadSession session, long candidate)
    {
        var transaction = new ControlledChatReadTransaction();
        transaction.Enqueue(session);
        session.AllowCommit();
        var service = new ChatRoomReadService(transaction, () => ReadAt);
        return service.MarkAsReadAsync(UserId, RoomId, new ChatRoomReadRequest(candidate));
    }
}
