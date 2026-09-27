namespace TranslaCat.Chat.Application.Read;

// readTime은 부작용 없는 시각 공급 함수다. no-op에서도 평가될 수 있지만 저장된 시각은 P1이 보존한다.
public sealed class ChatRoomReadService(IChatReadTransaction transaction, Func<DateTime> readTime)
{
    public Task<ChatRoomReadResponse> MarkAsReadAsync(
        long loginUserId,
        long? chatRoomId,
        ChatRoomReadRequest? request,
        CancellationToken cancellationToken = default)
    {
        // 잘못된 요청은 Domain의 null 후보 no-op과 다르며 원본 순서대로 거절한다.
        if (chatRoomId is null)
        {
            throw new ChatReadException("CHAT_ROOM_ID_REQUIRED", "채팅방 ID는 필수입니다.");
        }

        if (request?.LastReadMessageId is not long messageId)
        {
            throw new ChatReadException(
                "CHAT_ROOM_LAST_READ_MESSAGE_ID_REQUIRED",
                "마지막 읽은 메시지 ID는 필수입니다.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return transaction.ExecuteAsync(
            (session, token) => ReadWithinTransactionAsync(session, loginUserId, chatRoomId.Value, messageId, token),
            cancellationToken);
    }

    private async Task<ChatRoomReadResponse> ReadWithinTransactionAsync(
        IChatReadSession session,
        long loginUserId,
        long chatRoomId,
        long messageId,
        CancellationToken cancellationToken)
    {
        // no-op 여부를 보기 전에 OPEN 접근과 잠긴 활성 membership을 확인한다.
        await session.ValidateOpenRoomMemberAccessAsync(loginUserId, chatRoomId, cancellationToken);
        var member = await session.FindActiveMemberForUpdateAsync(chatRoomId, loginUserId, cancellationToken)
            ?? throw new ChatReadException(
                "CHAT_ROOM_MEMBER_ACCESS_DENIED",
                "채팅방 멤버가 아니거나 접근 권한이 없습니다.");

        // 같은/과거 ID도 소속·삭제·전송 상태·현재 가입 시점 검증을 통과해야 한다.
        var message = await session.FindMessageAsync(chatRoomId, messageId, cancellationToken);
        if (message is null || message.ChatRoomId != chatRoomId || message.DeletedAt is not null)
        {
            throw new ChatReadException("CHAT_ROOM_READ_MESSAGE_NOT_FOUND", "읽음 처리할 메시지를 찾을 수 없습니다.");
        }

        ValidateReadableMessage(member, message);
        cancellationToken.ThrowIfCancellationRequested();

        // 전진 판단은 P1에 위임한다. 갱신 전 snapshot은 방 이벤트의 previous 값으로 보존한다.
        var previousCursor = member.Cursor;
        var result = previousCursor.Advance(message.Id, readTime());
        if (result.Advanced)
        {
            await session.SaveAndFlushAsync(member, result.Cursor, cancellationToken);
        }

        // flush 이후의 집계 결과를 그대로 사용한다. 성공이나 no-op을 unread=0으로 치환하지 않는다.
        var unreadCount = await session.CountUnreadAsync(loginUserId, chatRoomId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var response = new ChatRoomReadResponse(
            member.ChatRoomId,
            result.Cursor.LastReadMessageId,
            result.Cursor.LastReadAt,
            unreadCount);

        // no-op도 자기 이벤트를 등록한다. 불변 값만 넘기며 commit 전에는 외부 전달하지 않는다.
        session.RegisterAfterCommit(new ChatReadUpdated(member.DestinationUsername, loginUserId, response));
        if (result.Advanced)
        {
            RegisterMemberReadUpdated(session, member, chatRoomId, previousCursor.LastReadMessageId, response);
        }

        return response;
    }

    private static void ValidateReadableMessage(ChatReadMember member, ChatReadMessage message)
    {
        // 원본처럼 SENT 실패가 가입 시점 실패보다 우선한다.
        if (!message.IsSent)
        {
            throw new ChatReadException(
                "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE",
                "전송 완료된 메시지만 읽음 처리할 수 있습니다.");
        }

        if (message.CreatedAt is null
            || member.JoinedAt is null
            || message.CreatedAt < member.JoinedAt)
        {
            throw new ChatReadException(
                "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE",
                "현재 참여 시점 이전 메시지는 읽음 처리할 수 없습니다.");
        }
    }

    private static void RegisterMemberReadUpdated(
        IChatReadSession session,
        ChatReadMember member,
        long chatRoomId,
        long? previousLastReadMessageId,
        ChatRoomReadResponse response)
    {
        // OPEN 공개 이벤트는 membership ID만 사용한다. 선택한 ID가 없으면 다른 ID로 대체하지 않는다.
        var openRoom = member.RoomType == ChatReadRoomType.Open;
        long? readerUserId = openRoom ? null : member.UserId;
        long? readerOpenChatMemberId = openRoom ? member.MemberId : null;
        if (readerUserId is null && readerOpenChatMemberId is null)
        {
            return;
        }

        session.RegisterAfterCommit(new ChatMemberReadUpdated(
            chatRoomId,
            readerUserId,
            readerOpenChatMemberId,
            previousLastReadMessageId,
            response.LastReadMessageId,
            response.LastReadAt));
    }
}
