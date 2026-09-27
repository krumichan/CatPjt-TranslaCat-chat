using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenModeration;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenModeration;

public sealed partial class EfOpenModerationStore(
    IDbContextFactory<ChatDbContext> contexts, ILogger<EfOpenModerationStore> logger,
    IChatRoomAccountReader? accounts = null, IChatMembershipDirectory? directory = null,
    IOpenProfileStorage? storage = null, IOpenMembershipDelivery? membershipEvents = null,
    IOpenBanEventDelivery? banEvents = null) : IOpenModerationStore
{
    public async Task<OpenProfile> ChangeAdminAsync(long actorId, long roomId, long memberId, bool assign, DateTime now, CancellationToken token)
    {
        // 다른 OPEN 변경과 같은 방 lock을 얻은 뒤 actor/target 상태를 현재 값으로 판정한다.
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var room = await LockRoomAsync(context, roomId, token);
        var actor = await LockActorAsync(context, actorId, roomId, token);
        if (actor.Role != "OWNER")
        {
            throw OpenRoomPolicy.Error("OPEN 채팅방 OWNER만 수행할 수 있습니다.", "OWNER_ONLY");
        }

        var target = await LockTargetAsync(context, memberId, roomId, token);
        if (assign && (actor.Id == target.Id || target.Role != "MEMBER"
            || await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId && ban.TargetUserId == target.UserId && ban.ReleasedAt == null, token)))
        {
            throw OpenRoomPolicy.Error("활성 MEMBER만 ADMIN으로 지정할 수 있습니다.", "ADMIN_TARGET_INVALID");
        }
        if (!assign && target.Role != "ADMIN")
        {
            throw OpenRoomPolicy.Error("활성 ADMIN 멤버만 ADMIN 역할을 해제할 수 있습니다.", "ADMIN_TARGET_INVALID");
        }

        // 응답 프로필과 전달 의존성까지 준비된 경우에만 DB 변경을 commit한다.
        var audit = await AuditAsync(actorId, token);
        target.Role = assign ? "ADMIN" : "MEMBER";
        target.UpdatedAt = now;
        target.UpdatedBy = audit;
        await context.SaveChangesAsync(token);
        var profile = await ProfileAsync(context, target.Id, token);
        var response = await MapProfileAsync(profile, target, token);
        var intent = new OpenMemberRoleChanged(roomId, target.Id, target.UserId, target.Role, actorId, room.Name, audit, now);
        var delivery = membershipEvents ?? throw new OpenRoomDependencyUnavailableException();
        await delivery.ValidateAvailabilityAsync([intent], token);
        await transaction.CommitAsync(token);

        // 동일 role 이벤트/활동 알림 경로를 재사용한다. 전송 실패가 권한 변경 rollback은 아니다.
        await DeliverSafelyAsync(() => delivery.DeliverAsync(intent, token));
        return response;
    }

    public async Task<OpenBanAction> BanAsync(long actorId, long roomId, long memberId, string reason, DateTime now, CancellationToken token)
    {
        // 방 lock을 가장 먼저 획득하여 동시 ban/leave/close가 과거 snapshot을 사용하지 않게 한다.
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var room = await LockRoomAsync(context, roomId, token);
        var actor = await LockActorAsync(context, actorId, roomId, token);
        OpenModerationPolicy.ValidateModerator(actor.Role);
        var target = await LockTargetAsync(context, memberId, roomId, token);
        OpenModerationPolicy.ValidateBanTarget(actor.Id, actor.Role, target.Id, target.Role);
        if (await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId && ban.TargetUserId == target.UserId && ban.ReleasedAt == null, token))
        {
            throw OpenRoomPolicy.Error("이미 해당 OPEN 채팅방에서 차단된 사용자입니다.", "BAN_ALREADY_ACTIVE");
        }

        // 계정과 감사 식별자는 소유 서비스에서 얻으며 미구성 상태에서는 변경하지 않는다.
        var profile = await ProfileAsync(context, target.Id, token);
        var audit = await AuditAsync(actorId, token);
        var recipient = await (directory ?? throw new OpenRoomDependencyUnavailableException()).FindByIdAsync(target.UserId, token);
        if (recipient is null || recipient.Id != target.UserId || string.IsNullOrWhiteSpace(recipient.Email))
        {
            throw new OpenRoomDependencyUnavailableException();
        }

        // 익명 프로필 snapshot과 재가입 차단을 같은 방 lock/transaction 안에 저장한다.
        var ban = new OpenChatBanEntity
        {
            ChatRoomId = roomId,
            TargetUserId = target.UserId,
            TargetChatRoomMemberId = target.Id,
            TargetMemberCode = profile.MemberCode,
            NicknameSnapshot = profile.Nickname,
            ProfileImageObjectKeySnapshot = ChatMessageText.IsBlank(profile.ProfileImageObjectKey) ? null : ChatMessageText.Trim(profile.ProfileImageObjectKey!),
            LastJoinedAtSnapshot = target.JoinedAt,
            TargetRoleSnapshot = target.Role,
            BannedByMemberId = actor.Id,
            BannedByRole = actor.Role,
            Reason = reason,
            BannedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = audit,
            UpdatedBy = audit
        };
        context.OpenChatBans.Add(ban);
        target.Role = "MEMBER";
        target.Active = false;
        target.LeftAt = now;
        target.LastReadMessageId = null;
        target.LastReadAt = null;
        target.UpdatedAt = now;
        target.UpdatedBy = audit;
        context.ChatMessages.Add(new ChatMessageEntity
        {
            ChatRoomId = roomId,
            SenderType = "SYSTEM",
            MessageType = "SYSTEM",
            Status = "SENT",
            Content = "OPEN 멤버 " + profile.MemberCode + "님이 운영 정책에 따라 강제 퇴장되었습니다.",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = audit,
            UpdatedBy = audit
        });
        await context.SaveChangesAsync(token);
        var delivery = banEvents ?? throw new OpenRoomDependencyUnavailableException();
        await delivery.ValidateAvailabilityAsync(token);
        var change = new OpenMemberBanned(roomId, ban.Id, target.Id, target.UserId, recipient.Email,
            actorId, room.Name, reason, now, audit, now);
        await transaction.CommitAsync(token);

        // private queue/방 이벤트/활동 알림은 DB commit 성공 이후의 별도 side effect다.
        await DeliverSafelyAsync(() => delivery.DeliverAsync(change, token));
        return new(roomId, ban.Id, target.Id, true, now, null);
    }

    public async Task<OpenBanAction> ReleaseAsync(long actorId, long roomId, long banId, DateTime now, CancellationToken token)
    {
        // 현재 moderator 권한과 ban에 보존된 당시 역할을 함께 확인한다.
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await LockRoomAsync(context, roomId, token);
        var actor = await LockActorAsync(context, actorId, roomId, token);
        OpenModerationPolicy.ValidateModerator(actor.Role);
        var ban = (await context.OpenChatBans.FromSqlInterpolated(
            $"SELECT * FROM open_chat_ban WHERE id = {banId} AND chat_room_id = {roomId} AND released_at IS NULL FOR UPDATE").ToListAsync(token))
            .SingleOrDefault() ?? throw OpenRoomPolicy.Error("활성 OPEN 채팅 차단 이력을 찾을 수 없습니다.", "BAN_NOT_FOUND");
        if (!OpenModerationPolicy.CanRelease(actor.Role, ban.BannedByRole, ban.TargetRoleSnapshot))
        {
            throw OpenRoomPolicy.Error("해당 차단 이력을 해제할 권한이 없습니다.", "BAN_RELEASE_FORBIDDEN");
        }

        // release는 차단 이력만 해제한다. membership을 자동 재활성화하거나 새 이벤트를 만들지 않는다.
        ban.ReleasedByMemberId = actor.Id;
        ban.ReleasedAt = now;
        ban.UpdatedAt = now;
        ban.UpdatedBy = await AuditAsync(actorId, token);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(roomId, ban.Id, ban.TargetChatRoomMemberId, false, ban.BannedAt, now);
    }

    private static async Task<ChatRoomEntity> LockRoomAsync(ChatDbContext context, long roomId, CancellationToken token)
    {
        var open = (await context.OpenChatRooms.FromSqlInterpolated(
            $"SELECT o.* FROM open_chat_room o JOIN chat_room r ON r.id = o.chat_room_id WHERE o.chat_room_id = {roomId} AND r.active = 1 AND r.deleted_at IS NULL FOR UPDATE").ToListAsync(token))
            .SingleOrDefault() ?? throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        if (open.Status == "CLOSED")
        {
            throw OpenRoomPolicy.Error("종료된 OPEN 채팅방에서는 운영 작업을 수행할 수 없습니다.", "ROOM_CLOSED");
        }

        return await context.ChatRooms.SingleAsync(room => room.Id == roomId, token);
    }

    private static async Task<ChatRoomMemberEntity> LockActorAsync(ChatDbContext context, long actorId, long roomId, CancellationToken token)
    {
        return (await context.ChatRoomMembers.FromSqlInterpolated(
            $"SELECT * FROM chat_room_member WHERE chat_room_id = {roomId} AND user_id = {actorId} AND active = 1 AND deleted_at IS NULL FOR UPDATE").ToListAsync(token))
        .SingleOrDefault() ?? throw OpenRoomPolicy.Error("OPEN 채팅방 운영 권한이 없습니다.", "MODERATION_ACCESS_DENIED");
    }

    private static async Task<ChatRoomMemberEntity> LockTargetAsync(ChatDbContext context, long memberId, long roomId, CancellationToken token)
    {
        if (memberId <= 0)
        {
            throw OpenRoomPolicy.Error("대상 OPEN 멤버 ID는 필수입니다.", "BAN_TARGET_REQUIRED");
        }

        return (await context.ChatRoomMembers.FromSqlInterpolated(
            $"SELECT * FROM chat_room_member WHERE id = {memberId} AND chat_room_id = {roomId} AND active = 1 AND deleted_at IS NULL FOR UPDATE").ToListAsync(token))
            .SingleOrDefault() ?? throw OpenRoomPolicy.Error("활성 OPEN 멤버를 찾을 수 없습니다.", "BAN_TARGET_INVALID");
    }

    private static async Task<OpenChatMemberProfileEntity> ProfileAsync(ChatDbContext context, long memberId, CancellationToken token)
    {
        return await context.OpenChatMemberProfiles.SingleOrDefaultAsync(profile => profile.ChatRoomMemberId == memberId, token)
        ?? throw OpenRoomPolicy.Error("OPEN 채팅 프로필을 찾을 수 없습니다.", "PROFILE_NOT_FOUND");
    }

    private async Task<OpenProfile> MapProfileAsync(OpenChatMemberProfileEntity profile, ChatRoomMemberEntity member, CancellationToken token)
    {
        return new(member.Id, profile.MemberCode, profile.Nickname, await UrlAsync(profile.ProfileImageObjectKey, token),
            member.Role, member.Active, null, member.JoinedAt);
    }

    private Task<string?> UrlAsync(string? key, CancellationToken token)
    {
        return ChatMessageText.IsBlank(key) ? Task.FromResult<string?>(null)
        : (storage ?? throw new OpenRoomDependencyUnavailableException()).ResolveUrlAsync(key!, token);
    }

    private async Task<string> AuditAsync(long actor, CancellationToken token)
    {
        var audit = await (accounts ?? throw new OpenRoomDependencyUnavailableException()).GetAuditIdentityAsync(actor, token);
        return audit is { Length: <= 50 } ? audit : throw new OpenRoomDependencyUnavailableException();
    }

    private async Task DeliverSafelyAsync(Func<Task> deliver)
    {
        try
        {
            await deliver();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("OPEN moderation post-commit delivery failed ({FailureType}).", exception.GetType().Name);
        }
    }
}
