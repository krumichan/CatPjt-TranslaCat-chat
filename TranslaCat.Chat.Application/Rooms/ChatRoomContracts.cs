namespace TranslaCat.Chat.Application.Rooms;

public sealed record ChatRoomCreateRequest(string? RoomType, string? Name, string? Description, IReadOnlyList<long?>? MemberUserIds);
public sealed record ChatDirectPartner(long UserId, string? PublicId, string? DisplayName,
    string? ProfileImageUrl, string? ProfileBackgroundImageUrl, string? Bio, bool? Online);
public sealed record ChatRoomView(long Id, string RoomType, string SourceType, string? Name, string? Description,
    long? OwnerId, long MemberCount, bool Active, string OriginalLanguageCode, string TranslationLanguageCode,
    bool RoomLanguageSettingApplied, DateTime CreatedAt, DateTime UpdatedAt, string MyRole, ChatDirectPartner? DirectPartner);
public sealed record ChatRoomListItem(long Id, string RoomType, string SourceType, string? Name, string? Description,
    long? OwnerId, long MemberCount, long UnreadCount, DateTime CreatedAt, DateTime UpdatedAt, ChatDirectPartner? DirectPartner);

public interface IChatRoomStore
{
    Task<long> CreateOrReuseAsync(long userId, ChatRoomCreateRequest request, IReadOnlyList<long?> distinctMembers, DateTime now, CancellationToken cancellationToken);
    Task<ChatRoomView> GetAsync(long userId, long roomId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatRoomListItem>> ListAsync(long userId, CancellationToken cancellationToken);
}

public interface IChatRoomAccountReader
{
    // 사용자 존재/프로필은 소유 서비스에서 확인한다. CHAT DB에 가짜 계정을 생성하지 않는다.
    Task EnsureUserExistsAsync(long? userId, CancellationToken cancellationToken);
    Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken);
    Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken cancellationToken);
}

public sealed class ChatRoomException(string message, string code = "") : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ChatRoomDependencyUnavailableException : Exception;
