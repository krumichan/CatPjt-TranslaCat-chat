namespace TranslaCat.Chat.Application.Presence;

public sealed record ChatPresenceRoomMember(long RoomId, string RoomType, long MemberId, bool PrivateAi);

public interface IChatPresenceRoomReader
{
    Task<IReadOnlyList<ChatPresenceRoomMember>> FindActiveMembershipsAsync(long userId, CancellationToken cancellationToken);
}

public interface IChatPresenceProfileReader
{
    // 계정 소유 서비스의 공개 식별자다. 내부 user ID를 문자열로 바꿔 대신하지 않는다.
    Task<string?> FindPublicIdAsync(long userId, CancellationToken cancellationToken);
}
