using TranslaCat.Chat.Infrastructure.Safety;
using TranslaCat.Chat.Application.Safety;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenInvitations;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenInvitations;

public sealed class EfOpenInvitationStore(IDbContextFactory<ChatDbContext> contexts, IChatMembershipDirectory directory,
    IChatActivityNotificationDelivery delivery, ILogger<EfOpenInvitationStore> logger,
    Func<(DateTime LocalNow, DateTime UtcNow)> clock, IChatContentPolicy? policy = null) : IOpenInvitationStore
{
    public const string SourcePrefix = "open-link:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OpenInvitationCreated> CreateAsync(long userId, long roomId, OpenInvitationValidatedCreate request,
        CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var prefetched = policy is null ? null : await directory.FindByPublicIdAsync(request.TargetPublicId, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (policy is not null)
            await ChatAccountWriteGate.AcquireAsync(db, prefetched is null ? [userId] : new[] { userId, prefetched.Id }, token);

        // 모든 초대는 actor gate → OPEN 방 row 순서로 잠근다. 신규 gate 경합도 upsert가 직렬화한다.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO chat_open_invitation_sender_gate (actor_user_id, touched_at_utc) VALUES ({userId}, UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE actor_user_id = actor_user_id", token);
        var gate = await db.OpenInvitationSenderGates.FromSqlInterpolated(
            $"SELECT * FROM chat_open_invitation_sender_gate WHERE actor_user_id = {userId} FOR UPDATE").SingleAsync(token);
        string requestPrefix = SourcePrefix + userId.ToString(CultureInfo.InvariantCulture) + ":" + request.RequestId.ToString("N") + ":";
        string sourceKey = requestPrefix + roomId.ToString(CultureInfo.InvariantCulture) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.TargetPublicId)));

        // 완료 요청은 방의 후속 변경과 무관하게 같은 결과를 돌려준다. 다른 대상/방으로 재사용할 수 없다.
        var previous = await db.ChatNotifications.AsNoTracking().SingleOrDefaultAsync(row => row.ActorUserId == userId
            && row.NotificationType == "CHAT_INVITATION" && row.SourceEventKey.StartsWith(requestPrefix), token);
        if (previous is not null)
        {
            if (previous.ChatRoomId != roomId || previous.SourceEventKey != sourceKey)
                throw Error("이 요청 식별자는 다른 초대에 사용됐습니다.", "REQUEST_CONFLICT");
            await transaction.CommitAsync(token);
            return new(previous.Id, roomId, previous.CreatedAt, true);
        }

        var open = await db.OpenChatRooms.FromSqlInterpolated(
            $"SELECT * FROM open_chat_room WHERE chat_room_id = {roomId} FOR UPDATE").SingleOrDefaultAsync(token);
        var room = await db.ChatRooms.SingleOrDefaultAsync(row => row.Id == roomId && row.Active && row.DeletedAt == null
            && row.RoomType == "OPEN", token);
        if (open is null || room is null) throw Error("초대할 OPEN 채팅방을 찾을 수 없습니다.", "NOT_ALLOWED");
        if (open.Status != "ACTIVE") throw Error("종료된 채팅방에는 초대할 수 없습니다.", "ROOM_CLOSED");

        // 링크 공유와 같은 활성 사람 참여자 권한이며, 방 전용 신원만 공개 payload에 사용한다.
        var sender = await (from member in db.ChatRoomMembers
                            join profile in db.OpenChatMemberProfiles on member.Id equals profile.ChatRoomMemberId
                            where member.ChatRoomId == roomId && member.UserId == userId && member.Active && member.DeletedAt == null
                            select new { Member = member, Profile = profile }).SingleOrDefaultAsync(token);
        if (sender is null || await db.OpenChatBans.AnyAsync(row => row.ChatRoomId == roomId
            && row.TargetUserId == userId && row.ReleasedAt == null, token))
            throw Error("현재 참여한 채팅방에서만 초대할 수 있습니다.", "MEMBER_ACCESS_DENIED");

        var target = await directory.FindByPublicIdAsync(request.TargetPublicId, token);
        if (target is null || target.Id == userId || await directory.IsBlockedBetweenAsync(userId, target.Id, token)
            || await db.OpenChatBans.AnyAsync(row => row.ChatRoomId == roomId && row.TargetUserId == target.Id && row.ReleasedAt == null, token)
            || await db.ChatRoomMembers.AnyAsync(row => row.ChatRoomId == roomId && row.UserId == target.Id && row.Active && row.DeletedAt == null, token))
            throw Error("해당 대상으로 초대를 보낼 수 없습니다.", "TARGET_UNAVAILABLE");
        if (policy is not null) ChatAccountWriteGate.RequireHeld(db, target.Id);
        var actor = await directory.FindByIdAsync(userId, token) ?? throw new OpenInvitationDependencyUnavailableException();

        // 잠금과 Core 조회를 기다린 뒤 현재 시각을 한 번 읽는다. 대기 전 시각으로 rate/TTL을 계산하지 않는다.
        var (now, utcNow) = clock();
        if (now.Kind != DateTimeKind.Unspecified || utcNow.Kind != DateTimeKind.Utc)
            throw new OpenInvitationDependencyUnavailableException();

        // gate를 가진 상태의 저장 기록으로 rolling 60초를 계산한다. 요청 ID를 바꿔도 우회할 수 없다.
        var recent = db.OpenInvitationPushOutbox.Where(row => row.SenderUserId == userId && row.CreatedAtUtc > utcNow.AddSeconds(-60));
        if (await recent.CountAsync(token) >= 10 || await recent.AnyAsync(row => row.RoomId == roomId
            && row.RecipientUserId == target.Id, token))
            throw Error("초대를 연속으로 보냈습니다. 잠시 후 다시 시도해 주세요.", "RATE_LIMITED");

        await delivery.ValidateAvailabilityAsync(token);
        var notification = new ChatNotificationEntity
        {
            RecipientUserId = target.Id, NotificationType = "CHAT_INVITATION", ChatRoomId = roomId, ActorUserId = userId,
            SourceEventKey = sourceKey, CreatedAt = now, CreatedBy = actor.Email,
            PayloadJson = JsonSerializer.Serialize(new { schemaVersion = 1, invitationKind = "OPEN_LINK", roomType = "OPEN",
                roomName = room.Name, inviterOpenNickname = sender.Profile.Nickname, inviterMemberCode = sender.Profile.MemberCode }, Json)
        };
        db.ChatNotifications.Add(notification);
        await db.SaveChangesAsync(token);

        // 초대 메시지·푸시 의도·rate gate는 같은 commit을 공유한다. 기존 메시지 outbox에는 가짜 메시지를 넣지 않는다.
        db.OpenInvitationPushOutbox.Add(new OpenInvitationPushOutboxEntity
        {
            NotificationId = notification.Id, RoomId = roomId, SenderUserId = userId, RecipientUserId = target.Id,
            EventId = Guid.NewGuid().ToString("D"), CreatedAtUtc = utcNow, DueAtUtc = utcNow
        });
        gate.TouchedAtUtc = utcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        // queue는 commit 뒤의 실시간 힌트다. 실패해도 저장된 카드/푸시를 재생성하지 않고 REST로 복원한다.
        try { await delivery.DeliverAsync(target.Email, Activity(notification), now, CancellationToken.None); }
        catch (Exception error) { logger.LogWarning("OPEN invitation realtime delivery failed. Failure={FailureType}", error.GetType().Name); }
        return new(notification.Id, roomId, notification.CreatedAt, false);
    }

    public async Task<IReadOnlyList<ChatNotificationActivity>> ListAsync(long userId, long? cursorId, int limit, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await Projection(Own(db, userId).Where(row => cursorId == null || row.Id < cursorId)
            .OrderByDescending(row => row.Id).Take(limit)).ToArrayAsync(token);
    }

    public async Task<OpenInvitationSummary> SummaryAsync(long userId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        long unread = await Own(db, userId).LongCountAsync(row => !row.IsRead, token);
        var latest = await Projection(Own(db, userId).OrderByDescending(row => row.Id)).FirstOrDefaultAsync(token);
        await transaction.CommitAsync(token);
        return new(unread, latest);
    }

    public async Task<ChatNotificationActivity?> GetAsync(long userId, long invitationId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await Projection(Own(db, userId).Where(row => row.Id == invitationId)).SingleOrDefaultAsync(token);
    }

    public async Task<ChatNotificationActivity?> ReadAsync(long userId, long invitationId, DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var own = Own(db, userId).Where(row => row.Id == invitationId);
        await own.Where(row => !row.IsRead).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsRead, true)
            .SetProperty(row => row.ReadAt, now), token);
        var result = await Projection(own).SingleOrDefaultAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private static IQueryable<ChatNotificationEntity> Own(ChatDbContext db, long userId) => db.ChatNotifications.AsNoTracking()
        .Where(row => row.RecipientUserId == userId && row.DeletedAt == null && row.NotificationType == "CHAT_INVITATION"
            && row.SourceEventKey.StartsWith(SourcePrefix));
    private static IQueryable<ChatNotificationActivity> Projection(IQueryable<ChatNotificationEntity> query) => query.Select(row =>
        new ChatNotificationActivity(row.Id, row.NotificationType, row.ChatRoomId, row.PayloadJson, row.IsRead, row.ReadAt, row.CreatedAt));
    private static ChatNotificationActivity Activity(ChatNotificationEntity row) =>
        new(row.Id, row.NotificationType, row.ChatRoomId, row.PayloadJson, row.IsRead, row.ReadAt, row.CreatedAt);
    private static OpenInvitationException Error(string message, string suffix) => OpenInvitationService.Error(message, suffix);
}
