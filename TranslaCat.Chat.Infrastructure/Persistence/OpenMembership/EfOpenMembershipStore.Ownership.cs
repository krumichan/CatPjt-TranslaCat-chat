using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenMembership;

public sealed partial class EfOpenMembershipStore
{
    public Task<OpenRoomDetail> TransferAsync(long userId, long roomId, long? targetMemberId, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(roomId, async (db, open, intents) =>
        {
            // 역할 변경과 방 ownerId 변경을 하나의 방 잠금/transaction으로 묶는다.
            EnsureOpen(open);
            var current = await ActiveMemberAsync(db, userId, roomId, token);
            EnsureOwner(current);
            if (targetMemberId is null or <= 0)
            {
                throw Error("OWNER 위임 대상 멤버는 필수입니다.", "OWNER_TRANSFER_TARGET_REQUIRED");
            }

            var target = await db.ChatRoomMembers.SingleOrDefaultAsync(row => row.Id == targetMemberId
                            && row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token);
            if (target is null || target.Id == current.Id)
            {
                throw Error(target is null ? "OWNER 위임 대상 멤버를 찾을 수 없습니다." : "자기 자신에게 OWNER를 위임할 수 없습니다.", "OWNER_TRANSFER_TARGET_INVALID");
            }

            var audit = await AuditAsync(userId, token);
            var room = await db.ChatRooms.SingleAsync(row => row.Id == roomId, token);
            current.Role = "MEMBER";
            target.Role = "OWNER";
            Touch(current, now, audit);
            Touch(target, now, audit);
            room.OwnerId = target.UserId;
            room.UpdatedAt = now;
            room.UpdatedBy = audit;
            await db.SaveChangesAsync(token);

            intents.Add(new OpenMemberRoleChanged(roomId, current.Id, current.UserId, current.Role, userId, room.Name, audit, now));
            intents.Add(new OpenMemberRoleChanged(roomId, target.Id, target.UserId, target.Role, userId, room.Name, audit, now));
            return await rooms.GetDetailWithinAsync(db, userId, roomId, token);
        }, token);
    }

    public Task<OpenRoomDetail> CloseAsync(long userId, long roomId, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(roomId, async (db, open, intents) =>
        {
            var owner = await ActiveMemberAsync(db, userId, roomId, token);
            EnsureOwner(owner);
            if (open.Status != "CLOSED")
            {
                // 이미 종료된 방은 시각/이벤트를 갱신하지 않는다. 멤버십과 기존 데이터도 보존한다.
                var audit = await AuditAsync(userId, token);
                open.Status = "CLOSED";
                open.ClosedAt = now;
                open.UpdatedAt = now;
                open.UpdatedBy = audit;
                var room = await db.ChatRooms.SingleAsync(row => row.Id == roomId, token);
                var recipients = await db.ChatRoomMembers.Where(row => row.ChatRoomId == roomId
                    && row.Active && row.DeletedAt == null && row.UserId != userId).Select(row => row.UserId).ToListAsync(token);
                await db.SaveChangesAsync(token);
                intents.Add(new OpenRoomClosed(roomId, now, userId, room.Name, recipients, audit, now));
            }
            return await rooms.GetDetailWithinAsync(db, userId, roomId, token);
        }, token);
    }
}
