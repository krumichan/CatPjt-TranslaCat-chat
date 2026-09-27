using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Membership;

public sealed record ChatMembershipUser(long Id, string Email, string? Username, string PublicId, string? Nickname, string? ProfileImageUrl);
public sealed record ChatMembershipRoom(long Id, string RoomType, string SourceType, string? Name);
public sealed record ChatMembershipMember(long Id, long UserId, string Role, DateTime JoinedAt);
public sealed record ChatMembershipTargets(IReadOnlyList<long?>? TargetUserIds, IReadOnlyList<string?>? TargetPublicIds);
public sealed record ChatGroupConversionRequest(string? Name, string? Description, ChatMembershipTargets Targets);
public sealed record ChatFriendGroupRequest(string? Name, string? Description, IReadOnlyList<long?>? MemberUserIds);
public sealed record ChatInvitedMember(long UserId, string PublicId, string? DisplayName, string? ProfileImageUrl, DateTime JoinedAt);
public sealed record ChatInvitationResult(long RoomId, bool CreatedNewGroupRoom, IReadOnlyList<ChatInvitedMember> InvitedMembers);

public interface IChatMembershipDirectory
{
    // 사용자/일반 프로필/친구/차단은 기존 소유 서비스의 최신 상태로 확인한다. CHAT DB로 복제하지 않는다.
    Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken cancellationToken);
    Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken);
    Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken cancellationToken);
    Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken cancellationToken);
}

public interface IChatMembershipStore
{
    Task<T> ExecuteAsync<T>(Func<IChatMembershipSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

public interface IChatMembershipSession
{
    Task<ChatMembershipRoom> LockRoomAsync(long roomId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMembershipMember>> GetActiveMembersAsync(long roomId, CancellationToken cancellationToken);
    Task<long?> GetLatestSentMessageIdAsync(long roomId, CancellationToken cancellationToken);
    Task<long?> FindFriendDirectAsync(long userId, long friendUserId, CancellationToken cancellationToken);
    Task<ChatMembershipRoom> CreateFriendDirectAsync(ChatMembershipUser owner, DateTime now, CancellationToken cancellationToken);
    Task<ChatMembershipRoom> CreateGroupAsync(string? name, string? description, ChatMembershipUser owner, string sourceType, DateTime now, CancellationToken cancellationToken);
    Task<ChatMembershipMember> AddOrRestoreAsync(ChatMembershipRoom room, ChatMembershipUser user, string role,
        long? initialCursor, string auditIdentity, DateTime now, CancellationToken cancellationToken);
    Task<ChatMessageView> InsertSystemMessageAsync(long roomId, string content, string auditIdentity, DateTime now, CancellationToken cancellationToken);
    void RegisterAfterCommit(ChatMembershipIntent intent);
}

public abstract record ChatMembershipIntent;
public sealed record ChatMembershipMessageCreated(ChatMessageView Message) : ChatMembershipIntent;
public sealed record ChatMembershipChanged(long RoomId, DateTime OccurredAt) : ChatMembershipIntent;
public sealed record ChatMembershipInvitationCommitted(long RoomId, string? RoomName, long RecipientUserId, string RecipientEmail,
    long ActorUserId, long MemberId, DateTime JoinedAt, string AuditIdentity) : ChatMembershipIntent;

public interface IChatMembershipEventDelivery
{
    Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken cancellationToken);
    Task DeliverAsync(ChatMembershipIntent intent, CancellationToken cancellationToken);
}

public sealed class ChatMembershipException(string message, string code = "") : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class ChatMembershipDependencyUnavailableException : Exception;
