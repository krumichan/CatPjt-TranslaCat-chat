namespace TranslaCat.Chat.Application.OpenRooms;

public sealed record OpenOwnerProfile(string? Nickname, string? ProfileImageObjectKey);
public sealed record OpenRoomCreate(string? Name, string? Description, string? Visibility,
    int? MaxMemberCount, OpenOwnerProfile? OwnerProfile);
public sealed record OpenRoomValidatedCreate(string Name, string Description, string Visibility,
    int MaxMemberCount, string Nickname, string? ProfileImageObjectKey);
public sealed record OpenProfile(long OpenChatMemberId, string MemberCode, string Nickname,
    string? ProfileImageUrl, string Role, bool Active, bool? Online, DateTime JoinedAt);
public sealed record OpenRoomAiSummary(bool AiEnabled, int AiMemberCount, string? DisclosureType);
public sealed record OpenAiDisplayMember(long AiMemberId, string Nickname, string? ProfileImageUrl,
    string Role, bool Active, DateTime JoinedAt);
public sealed record OpenMemberList(IReadOnlyList<OpenProfile> Members,
    IReadOnlyList<OpenAiDisplayMember> AiMembers, string? AiDisclosureType);
public sealed record OpenRoomDetail(long Id, string RoomType, string SourceType, string? Name,
    string? Description, string Visibility, string Status, long MemberCount, int MaxMemberCount,
    bool Joined, bool Joinable, string JoinBlockedReason, string? MyRole,
    OpenProfile? OwnerProfile, OpenProfile? MyOpenProfile, DateTime LastActivityAt,
    DateTime CreatedAt, DateTime UpdatedAt, OpenRoomAiSummary Ai);
public sealed record OpenRoomListItem(long Id, string RoomType, string SourceType, string? Name,
    string? Description, string Visibility, string Status, long MemberCount, int MaxMemberCount,
    bool Joined, bool Joinable, string JoinBlockedReason, DateTime LastActivityAt,
    OpenProfile? OwnerProfile, OpenRoomAiSummary Ai);
public sealed record OpenRoomList(IReadOnlyList<OpenRoomListItem> Rooms, long? NextCursorId, bool HasNext);
public sealed record OpenProfileChanged(long RoomId, long OpenChatMemberId, string MemberCode,
    string Nickname, string? ProfileImageUrl, string Role, DateTime OccurredAt);

public interface IOpenRoomStore
{
    Task<OpenRoomDetail> CreateAsync(long userId, OpenRoomValidatedCreate request, DateTime now, CancellationToken cancellationToken);
    Task<OpenRoomDetail> GetDetailAsync(long userId, long roomId, CancellationToken cancellationToken);
    Task<OpenRoomList> ListAsync(long userId, string? keyword, long? cursorId, int size, CancellationToken cancellationToken);
    Task<OpenProfile> GetProfileAsync(long userId, long roomId, long? memberId, CancellationToken cancellationToken);
    Task<OpenMemberList> GetMembersAsync(long userId, long roomId, CancellationToken cancellationToken);
    Task<OpenProfile> UpdateNicknameAsync(long userId, long roomId, string? nickname, DateTime now, CancellationToken cancellationToken);
    Task<OpenProfile> DeleteImageAsync(long userId, long roomId, DateTime now, CancellationToken cancellationToken);
}

public interface IOpenProfileStorage
{
    // URL/삭제 권한은 공통 storage 소유 서비스의 계약이다. object key를 임의 URL로 조합하지 않는다.
    Task<string?> ResolveUrlAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public interface IOpenProfileEventDelivery
{
    bool IsConfigured
    {
        get;
    }
    Task DeliverAsync(OpenProfileChanged change, CancellationToken cancellationToken);
}

public sealed class OpenRoomException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class OpenRoomDependencyUnavailableException : Exception;
