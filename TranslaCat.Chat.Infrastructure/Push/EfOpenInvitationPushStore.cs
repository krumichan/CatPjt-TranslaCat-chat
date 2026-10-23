using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Application.Safety;

namespace TranslaCat.Chat.Infrastructure.Push;

public sealed class EfOpenInvitationPushStore(IDbContextFactory<ChatDbContext> contexts, ChatPushOptions options,
    IChatContentPolicy? contentPolicy = null) : IOpenInvitationPushStore
{
    public async Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);

        // 만료 작업은 외부 호출 없이 종료한다. 진행 중인 유효 lease는 건드리지 않는다.
        if (deliveries)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE open_invitation_push_delivery SET status = 'EXPIRED', lease_token = NULL, lease_expires_at_utc = NULL
                WHERE status IN ('SEND','RECEIPT','INVALIDATE')
                  AND COALESCE(receipt_expires_at_utc, TIMESTAMPADD(HOUR, 24, created_at_utc)) <= UTC_TIMESTAMP(6)
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
                """, token);
            return await db.Database.SqlQuery<long>($"""
                SELECT id AS Value FROM open_invitation_push_delivery
                WHERE status IN ('SEND','RECEIPT','INVALIDATE') AND due_at_utc <= UTC_TIMESTAMP(6)
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
                ORDER BY due_at_utc, id LIMIT {options.BatchSize}
                """).ToListAsync(token);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_outbox SET status = 'EXPIRED', lease_token = NULL, lease_expires_at_utc = NULL
            WHERE status = 'PENDING' AND created_at_utc <= TIMESTAMPADD(HOUR, -24, UTC_TIMESTAMP(6))
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return await db.Database.SqlQuery<long>($"""
            SELECT id AS Value FROM open_invitation_push_outbox WHERE status = 'PENDING' AND due_at_utc <= UTC_TIMESTAMP(6)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            ORDER BY due_at_utc, id LIMIT {options.BatchSize}
            """).ToListAsync(token);
    }

    public async Task<OpenInvitationPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        string lease = Guid.NewGuid().ToString("D");
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_outbox SET lease_token = {lease},
                lease_expires_at_utc = TIMESTAMPADD(SECOND, {options.LeaseSeconds}, UTC_TIMESTAMP(6)), attempts = attempts + 1
            WHERE id = {id} AND status = 'PENDING' AND due_at_utc <= UTC_TIMESTAMP(6)
              AND created_at_utc > TIMESTAMPADD(HOUR, -24, UTC_TIMESTAMP(6))
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return changed == 0 ? null : await db.OpenInvitationPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id && row.LeaseToken == lease, token);
    }

    public async Task<OpenInvitationPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        string lease = Guid.NewGuid().ToString("D");
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_delivery SET lease_token = {lease},
                lease_expires_at_utc = TIMESTAMPADD(SECOND, {options.LeaseSeconds}, UTC_TIMESTAMP(6)), attempts = attempts + 1
            WHERE id = {id} AND status IN ('SEND','RECEIPT','INVALIDATE') AND due_at_utc <= UTC_TIMESTAMP(6)
              AND COALESCE(receipt_expires_at_utc, TIMESTAMPADD(HOUR, 24, created_at_utc)) > UTC_TIMESTAMP(6)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return changed == 0 ? null : await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == id && row.LeaseToken == lease, token);
    }

    public async Task<OpenInvitationPushOutboxEntity> OutboxAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.OpenInvitationPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id, token);
    }

    public async Task<bool> EligibleAsync(OpenInvitationPushOutboxEntity job, CancellationToken token)
    {
        if (job.Id <= 0 || job.NotificationId <= 0 || job.RoomId <= 0 || job.SenderUserId <= 0
            || job.RecipientUserId <= 0 || job.SenderUserId == job.RecipientUserId
            || !Guid.TryParseExact(job.EventId, "D", out var eventId) || eventId.ToString("D") != job.EventId)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(token);

        // 초대 수신함은 보존하고 전역 push 정책만 적용한다. 비참여자 초대에 방별 mute를 적용하지 않는다.
        if (contentPolicy is not null && await db.ChatAccountDeletions.AnyAsync(row => row.UserId == job.SenderUserId
            || row.UserId == job.RecipientUserId, token)) return false;
        if (await db.ChatNotificationPreferences.AnyAsync(row => row.UserId == job.RecipientUserId
            && (!row.PushEnabled || (row.EnabledSinceUtc != null && row.EnabledSinceUtc > job.CreatedAtUtc)), token)) return false;

        // push identity를 원본 알림과 대조한다. 이미 확인한 초대나 지워진 알림은 다시 울리지 않는다.
        var notification = await db.ChatNotifications.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == job.NotificationId && row.RecipientUserId == job.RecipientUserId
            && row.ActorUserId == job.SenderUserId && row.ChatRoomId == job.RoomId
            && row.NotificationType == "CHAT_INVITATION" && !row.IsRead && row.ReadAt == null
            && row.DeletedAt == null, token);
        if (notification is null || !HasOpenLinkPayload(notification.PayloadJson)) return false;

        // OPEN 자체의 종료 상태와 현재 사람 멤버/ban을 조회한다. 재가입한 발신자의 예전 초대도 재사용하지 않는다.
        return await (from room in db.ChatRooms
                      join open in db.OpenChatRooms on room.Id equals open.ChatRoomId
                      where room.Id == job.RoomId && room.RoomType == "OPEN" && room.Active && room.DeletedAt == null
                          && open.Status == "ACTIVE"
                          && db.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id
                              && member.UserId == job.SenderUserId && member.Active && member.DeletedAt == null
                              && member.JoinedAt <= notification.CreatedAt)
                          && !db.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id
                              && member.UserId == job.RecipientUserId && member.Active && member.DeletedAt == null)
                          && !db.OpenChatBans.Any(ban => ban.ChatRoomId == room.Id && ban.ReleasedAt == null
                              && (ban.TargetUserId == job.SenderUserId || ban.TargetUserId == job.RecipientUserId))
                      select room.Id).AnyAsync(token);
    }

    private static bool HasOpenLinkPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > 16384) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 4 });
            var value = json.RootElement;
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (value.EnumerateObject().Any(property => !names.Add(property.Name))) return false;
            return value.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == System.Text.Json.JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 1
                && value.TryGetProperty("invitationKind", out var kind) && kind.ValueKind == System.Text.Json.JsonValueKind.String
                && kind.GetString() == "OPEN_LINK"
                && value.TryGetProperty("roomType", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String
                && type.GetString() == "OPEN";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public async Task<bool> ExpandAsync(OpenInvitationPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (targets.Any(target => target.UserId != job.RecipientUserId)
            || targets.Select(target => target.InstallationId).Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            throw new InvalidOperationException("Invitation push targets do not match the recipient.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(token);

        // 상태 전이와 설치별 유일 행 삽입이 함께 commit된다. 만료/대체 lease의 늦은 결과는 버린다.
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_outbox SET status = 'EXPANDED', lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = 'PENDING' AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token);
        if (changed == 0) return false;

        db.OpenInvitationPushDeliveries.AddRange(targets.Select(target => new OpenInvitationPushDeliveryEntity
        {
            OutboxId = job.Id,
            InstallationId = target.InstallationId,
            AccountBindingId = target.AccountBindingId,
            Generation = target.Generation,
            TokenVersion = target.TokenVersion,
            CreatedAtUtc = job.CreatedAtUtc,
            DueAtUtc = DateTime.UtcNow
        }));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> FinishOutboxAsync(OpenInvitationPushOutboxEntity job, string status, int delaySeconds, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_outbox SET status = {status}, due_at_utc = TIMESTAMPADD(SECOND, {delaySeconds}, UTC_TIMESTAMP(6)),
                lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = 'PENDING' AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token) == 1;
    }

    public async Task<bool> FinishDeliveryAsync(OpenInvitationPushDeliveryEntity job, string status, int delaySeconds,
        string? ticketId, bool resetAttempts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_delivery SET status = {status}, due_at_utc = TIMESTAMPADD(SECOND, {delaySeconds}, UTC_TIMESTAMP(6)),
                ticket_id = {ticketId}, attempts = CASE WHEN {resetAttempts} THEN 0 ELSE attempts END,
                receipt_expires_at_utc = CASE WHEN {job.Status} = 'SEND' AND {status} = 'RECEIPT'
                    THEN TIMESTAMPADD(HOUR, 24, UTC_TIMESTAMP(6)) ELSE receipt_expires_at_utc END,
                lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = {job.Status} AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token) == 1;
    }

    public async Task<bool> RefreshTargetAsync(OpenInvitationPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token)
    {
        if (!OpenInvitationPushProcessor.Matches(job, target)) return false;
        await using var db = await contexts.CreateDbContextAsync(token);

        // 같은 계정/sid binding과 tokenVersion의 resume generation만 전진시킨다.
        // 실제 전송 snapshot을 먼저 영속화하여 늦은 receipt가 이 네 값으로만 무효화하도록 한다.
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE open_invitation_push_delivery SET generation = {target.Generation},
                lease_expires_at_utc = TIMESTAMPADD(SECOND, {options.LeaseSeconds}, UTC_TIMESTAMP(6))
            WHERE id = {job.Id} AND status = 'SEND' AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6) AND installation_id = {target.InstallationId}
              AND account_binding_id = {target.AccountBindingId} AND token_version = {target.TokenVersion}
              AND generation <= {target.Generation}
            """, token);
        if (changed == 0) return false;
        job.Generation = target.Generation;
        return true;
    }
    public async Task<bool> OwnsDeliveryAsync(OpenInvitationPushDeliveryEntity job, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.OpenInvitationPushDeliveries.FromSqlInterpolated($"""
            SELECT * FROM open_invitation_push_delivery WHERE id = {job.Id} AND status = {job.Status}
              AND lease_token = {job.LeaseToken} AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """).AnyAsync(token);
    }
}
