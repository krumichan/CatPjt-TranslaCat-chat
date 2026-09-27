using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Application.OpenModeration;

public sealed record OpenBanRequest(long? TargetOpenChatMemberId, string? Reason);
public sealed record OpenBanAction(long RoomId, long BanId, long TargetOpenChatMemberId, bool Active, DateTime BannedAt, DateTime? ReleasedAt);
public sealed record OpenBanActor(long OpenChatMemberId, string Nickname, string Role);
public sealed record OpenBanItem(long BanId, long TargetOpenChatMemberId, string MemberCode, string Nickname,
    string? ProfileImageUrl, DateTime LastJoinedAt, DateTime BannedAt, OpenBanActor BannedBy, string Reason, bool Releasable);
public sealed record OpenBanList(IReadOnlyList<OpenBanItem> Items, long? NextCursorId, bool HasNext);
public sealed record OpenMemberBanned(long RoomId, long BanId, long MemberId, long UserId, string Username,
    long ActorUserId, string? RoomName, string Reason, DateTime BannedAt, string AuditIdentity, DateTime OccurredAt);

public interface IOpenModerationStore
{
    Task<OpenProfile> ChangeAdminAsync(long actorId, long roomId, long memberId, bool assign, DateTime now, CancellationToken token);
    Task<OpenBanAction> BanAsync(long actorId, long roomId, long memberId, string reason, DateTime now, CancellationToken token);
    Task<OpenBanAction> ReleaseAsync(long actorId, long roomId, long banId, DateTime now, CancellationToken token);
    Task<OpenBanList> ListAsync(long actorId, long roomId, string? keyword, long? cursorId, int? size, CancellationToken token);
}

public interface IOpenBanEventDelivery
{
    Task ValidateAvailabilityAsync(CancellationToken token);
    Task DeliverAsync(OpenMemberBanned change, CancellationToken token);
}

public sealed class OpenModerationService(IOpenModerationStore store, Func<DateTime> clock)
{
    public Task<OpenProfile> ChangeAdminAsync(long userId, long roomId, long memberId, bool assign, CancellationToken token)
    {
        return store.ChangeAdminAsync(userId, roomId, memberId, assign, clock(), token);
    }

    public Task<OpenBanAction> BanAsync(long userId, long roomId, OpenBanRequest? request, CancellationToken token)
    {
        // 원본은 ban 요청을 방 lock/권한 조회보다 먼저 검증한다.
        if (request is null)
        {
            throw OpenRoomPolicy.Error("강제 퇴장 요청은 필수입니다.", "BAN_REQUEST_REQUIRED");
        }
        if (request.TargetOpenChatMemberId is null or <= 0)
        {
            throw OpenRoomPolicy.Error("강제 퇴장 대상 OPEN 멤버 ID는 필수입니다.", "BAN_TARGET_REQUIRED");
        }

        return store.BanAsync(userId, roomId, request.TargetOpenChatMemberId.Value,
            OpenModerationPolicy.NormalizeReason(request.Reason), clock(), token);
    }

    public Task<OpenBanAction> ReleaseAsync(long userId, long roomId, long banId, CancellationToken token)
    {
        return store.ReleaseAsync(userId, roomId, banId, clock(), token);
    }

    public Task<OpenBanList> ListAsync(long userId, long roomId, string? keyword, long? cursorId, int? size, CancellationToken token)
    {
        return store.ListAsync(userId, roomId, keyword, cursorId, size, token);
    }
}

public static class OpenModerationPolicy
{
    public static string NormalizeReason(string? reason)
    {
        if (ChatMessageText.IsBlank(reason))
        {
            throw OpenRoomPolicy.Error("강제 퇴장 사유는 필수입니다.", "BAN_REASON_REQUIRED");
        }

        var normalized = ChatMessageText.Trim(reason!);
        if (normalized.Length > 500)
        {
            throw OpenRoomPolicy.Error("강제 퇴장 사유는 500자 이하여야 합니다.", "BAN_REASON_TOO_LONG");
        }

        return normalized;
    }

    public static void ValidateModerator(string role)
    {
        if (role is not ("OWNER" or "ADMIN"))
        {
            throw OpenRoomPolicy.Error("OPEN 채팅방 OWNER 또는 ADMIN만 수행할 수 있습니다.", "MODERATION_ACCESS_DENIED");
        }
    }

    public static void ValidateBanTarget(long actorId, string actorRole, long targetId, string targetRole)
    {
        // 동일 대상 판정을 먼저 유지하고, 그 뒤 대상 역할의 보호 규칙을 적용한다.
        if (actorId == targetId)
        {
            throw OpenRoomPolicy.Error("자기 자신을 강제 퇴장시킬 수 없습니다.", "BAN_SELF_NOT_ALLOWED");
        }

        if (targetRole == "OWNER")
        {
            throw OpenRoomPolicy.Error("OWNER는 강제 퇴장 대상이 될 수 없습니다.", "BAN_ROLE_FORBIDDEN");
        }

        if (actorRole == "ADMIN" && targetRole != "MEMBER")
        {
            throw OpenRoomPolicy.Error("ADMIN은 MEMBER만 강제 퇴장시킬 수 있습니다.", "BAN_ROLE_FORBIDDEN");
        }
    }

    public static bool CanRelease(string actorRole, string bannedByRole, string targetRole)
    {
        return actorRole == "OWNER" || (actorRole == "ADMIN" && bannedByRole == "ADMIN" && targetRole == "MEMBER");
    }
}
