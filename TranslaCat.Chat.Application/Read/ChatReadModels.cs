using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.Application.Read;

public enum ChatReadRoomType
{
    Direct,
    Group,
    Open
}

public sealed record ChatRoomReadRequest(long? LastReadMessageId);

public sealed record ChatRoomReadResponse(
    long ChatRoomId,
    long? LastReadMessageId,
    DateTime? LastReadAt,
    long UnreadCount);

// 잠긴 활성 membership의 읽음 처리에 필요한 snapshot만 가져온다.
public sealed record ChatReadMember(
    long? MemberId,
    long ChatRoomId,
    long? UserId,
    string? DestinationUsername,
    ChatReadRoomType RoomType,
    DateTime? JoinedAt,
    ReadCursor Cursor);

public sealed record ChatReadMessage(
    long Id,
    long ChatRoomId,
    DateTime? DeletedAt,
    bool IsSent,
    DateTime? CreatedAt);
