using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Application.OpenMembership;

public sealed record OpenMembershipResult(long RoomId, bool Joined, string MyRole, OpenProfile MyOpenProfile);

public interface IOpenMembershipStore
{
    Task<OpenRoomDetail> JoinAsync(long userId, long roomId, OpenOwnerProfile? profile, DateTime now, CancellationToken token);
    Task<OpenMembershipResult> LeaveAsync(long userId, long roomId, DateTime now, CancellationToken token);
    Task<OpenRoomDetail> TransferAsync(long userId, long roomId, long? targetMemberId, DateTime now, CancellationToken token);
    Task<OpenRoomDetail> CloseAsync(long userId, long roomId, DateTime now, CancellationToken token);
}

public sealed class OpenMembershipService(IOpenMembershipStore store, Func<DateTime> localNow)
{
    // 방 잠금 뒤 권한/상태를 확인해야 하므로 no-op 판단도 영속 transaction 안에서 수행한다.
    public Task<OpenRoomDetail> JoinAsync(long userId, long roomId, OpenOwnerProfile? profile, CancellationToken token)
    {
        return store.JoinAsync(userId, roomId, profile, localNow(), token);
    }

    public Task<OpenMembershipResult> LeaveAsync(long userId, long roomId, CancellationToken token)
    {
        return store.LeaveAsync(userId, roomId, localNow(), token);
    }

    public Task<OpenRoomDetail> TransferAsync(long userId, long roomId, long? targetMemberId, CancellationToken token)
    {
        return store.TransferAsync(userId, roomId, targetMemberId, localNow(), token);
    }

    public Task<OpenRoomDetail> CloseAsync(long userId, long roomId, CancellationToken token)
    {
        return store.CloseAsync(userId, roomId, localNow(), token);
    }
}

public abstract record OpenMembershipIntent(long RoomId, DateTime OccurredAt);
public sealed record OpenMembersChanged(long RoomId, DateTime OccurredAt) : OpenMembershipIntent(RoomId, OccurredAt);
public sealed record OpenMemberProfileChanged(OpenProfileChanged Profile) : OpenMembershipIntent(Profile.RoomId, Profile.OccurredAt);
public sealed record OpenMemberRoleChanged(long RoomId, long MemberId, long UserId, string Role,
    long ActorUserId, string? RoomName, string AuditIdentity, DateTime OccurredAt) : OpenMembershipIntent(RoomId, OccurredAt);
public sealed record OpenRoomClosed(long RoomId, DateTime ClosedAt, long ActorUserId, string? RoomName,
    IReadOnlyList<long> RecipientUserIds, string AuditIdentity, DateTime OccurredAt) : OpenMembershipIntent(RoomId, OccurredAt);

public interface IOpenMembershipDelivery
{
    Task ValidateAvailabilityAsync(IReadOnlyList<OpenMembershipIntent> intents, CancellationToken token);
    Task DeliverAsync(OpenMembershipIntent intent, CancellationToken token);
}
