using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Infrastructure.Persistence.ProfileImages;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

public sealed partial class EfOpenRoomStore : IOpenProfileImageStore
{
    public async Task<OpenProfile> UploadImageAsync(long userId, long roomId, ChatProfileImageUpload upload,
        DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        IChatProfileImageObjectStore? objects = null;
        string? newKey = null;
        string? oldKey = null;
        bool commitAttempted = false;
        OpenProfile result;
        OpenProfileChanged change;
        try
        {
            // nickname/DELETE와 같은 OPEN lock 순서와 원본 ban→membership→closed 검사를 사용한다.
            var locked = await db.OpenChatRooms.FromSqlInterpolated($"SELECT o.* FROM open_chat_room o JOIN chat_room r ON r.id = o.chat_room_id WHERE o.chat_room_id = {roomId} AND r.active = 1 AND r.deleted_at IS NULL FOR UPDATE")
                .ToListAsync(token);
            var member = await ActiveMemberAsync(db, userId, roomId, token);
            var open = locked.SingleOrDefault() ?? throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
            if (open.Status == "CLOSED")
            {
                throw OpenRoomPolicy.Error("종료된 OPEN 채팅방에서는 해당 작업을 수행할 수 없습니다.", "ROOM_CLOSED");
            }
            var row = await MyProfileAsync(db, member.Id, token);
            var validated = ChatProfileImagePolicy.Validate(upload, ChatProfileImageKind.Profile);
            objects = storage as IChatProfileImageObjectStore ?? throw new ChatProfileImageUnavailableException();
            if (events?.IsConfigured != true)
            {
                throw new OpenRoomDependencyUnavailableException();
            }
            var audit = await AuditAsync(userId, token);

            oldKey = row.Profile.ProfileImageObjectKey;
            newKey = ChatProfileImagePolicy.ObjectKey(member.Id, true, ChatProfileImageKind.Profile, validated.Extension);
            await objects.StoreAsync(newKey, validated.ContentType, validated.Bytes, token);
            row.Profile.ProfileImageObjectKey = newKey;
            row.Profile.UpdatedAt = now;
            row.Profile.UpdatedBy = audit;
            await db.SaveChangesAsync(token);
            result = await MapProfileAsync(row, null, token);
            change = new(roomId, member.Id, row.Profile.MemberCode, result.Nickname, result.ProfileImageUrl, member.Role, now);
            commitAttempted = true;
            await transaction.CommitAsync(token);
        }
        catch
        {
            await ChatProfileImageCleanup.RollbackAsync(transaction, commitAttempted, objects, newKey, logger);
            throw;
        }

        // commit 전에는 기존 object를 지우지 않는다. cleanup 실패는 새로 저장된 profile을 되돌리지 않는다.
        await ChatProfileImageCleanup.DeleteAsync(objects, oldKey, logger);
        try
        {
            await events!.DeliverAsync(change, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("OPEN image post-commit event failed ({FailureType}).", exception.GetType().Name);
        }
        return result;
    }
}
