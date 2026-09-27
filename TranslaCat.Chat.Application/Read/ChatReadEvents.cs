namespace TranslaCat.Chat.Application.Read;

// 개인 recipient와 payload의 user ID는 서로 다른 용도다. wire DTO가 아닌 발행 의도다.
public sealed record ChatReadUpdated(
    string? DestinationUsername,
    long UserId,
    ChatRoomReadResponse Response);

// occurredAt은 원본처럼 commit 후 transport DTO 작성 시 결정하며 여기서 생성하지 않는다.
public sealed record ChatMemberReadUpdated(
    long ChatRoomId,
    long? ReaderUserId,
    long? ReaderOpenChatMemberId,
    long? PreviousLastReadMessageId,
    long? LastReadMessageId,
    DateTime? ReadAt);
