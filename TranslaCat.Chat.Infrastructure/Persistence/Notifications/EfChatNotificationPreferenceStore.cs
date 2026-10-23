using TranslaCat.Chat.Infrastructure.Safety;
using TranslaCat.Chat.Application.Safety;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.NotificationPreferences;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Notifications;

public sealed class EfChatNotificationPreferenceStore(IDbContextFactory<ChatDbContext> contexts, IChatContentPolicy? policy = null) : IChatNotificationPreferenceStore
{
    public async Task<ChatNotificationPreference> GetAsync(long userId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        bool? enabled = await db.ChatNotificationPreferences.AsNoTracking().Where(row => row.UserId == userId)
            .Select(row => (bool?)row.PushEnabled).SingleOrDefaultAsync(token);
        return new(enabled ?? true);
    }

    public async Task<ChatNotificationPreference> PutAsync(long userId, bool pushEnabled, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);

        await using var transaction = policy is null ? null : await db.Database.BeginTransactionAsync(token);
        if (policy is not null) await ChatAccountWriteGate.AcquireAsync(db, [userId], token);

        // 한 SQL의 행 잠금으로 동시 PUT을 직렬화한다. 같은 값 재전송은 cutoff를 변경하지 않는다.
        // 다시 켠 시점 이전의 대기 알림은 보내지 않지만 안읽음 데이터는 그대로 남긴다.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_notification_preference (user_id, push_enabled, enabled_since_utc)
            VALUES ({userId}, {pushEnabled}, NULL)
            ON DUPLICATE KEY UPDATE
                enabled_since_utc = CASE WHEN push_enabled = b'0' AND {pushEnabled}
                    THEN UTC_TIMESTAMP(6) ELSE enabled_since_utc END,
                push_enabled = {pushEnabled}
            """, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return new(pushEnabled);
    }

    public async Task<ChatRoomNotificationPreference?> GetRoomAsync(long userId, long roomId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await AccessibleMembers(db, userId, roomId).AsNoTracking()
            .Select(row => new ChatRoomNotificationPreference(roomId, row.NotificationMuted)).SingleOrDefaultAsync(token);
    }

    public async Task<ChatRoomNotificationPreference?> PutRoomAsync(long userId, long roomId, bool muted, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
        if (policy is not null) await ChatAccountWriteGate.AcquireAsync(db, [userId], token);

        // 방 lifecycle과 같은 방→멤버 순서로 잠근다. 탈퇴·강퇴 뒤의 요청이 설정을 바꾸지 못하게 한다.
        var room = await db.ChatRooms.FromSqlInterpolated($"SELECT * FROM chat_room WHERE id = {roomId} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        if (room is null) return null;
        var member = await AccessibleMembers(db, userId, roomId).AsNoTracking().SingleOrDefaultAsync(token);
        if (member is null) return null;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_room_member
            SET notification_enabled_since_utc = CASE WHEN notification_muted = b'1' AND NOT {muted}
                    THEN UTC_TIMESTAMP(6) ELSE notification_enabled_since_utc END,
                notification_muted = {muted}
            WHERE id = {member.Id} AND user_id = {userId}
            """, token);
        await transaction.CommitAsync(token);
        return new(roomId, muted);
    }

    private static IQueryable<ChatRoomMemberEntity> AccessibleMembers(ChatDbContext db, long userId, long roomId) =>
        from member in db.ChatRoomMembers
        join room in db.ChatRooms on member.ChatRoomId equals room.Id
        where room.Id == roomId && room.Active && room.DeletedAt == null
            && member.UserId == userId && member.Active && member.DeletedAt == null
            && (room.RoomType != "OPEN" ||
                (db.OpenChatRooms.Any(open => open.ChatRoomId == room.Id && open.Status == "ACTIVE")
                && !db.OpenChatBans.Any(ban => ban.ChatRoomId == room.Id && ban.TargetUserId == userId && ban.ReleasedAt == null)))
        select member;
}
