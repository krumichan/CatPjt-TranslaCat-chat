using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Push;

public sealed class EfChatPushStore(IDbContextFactory<ChatDbContext> contexts, ChatPushOptions options) : IChatPushStore
{
    public async Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);

        // 만료 작업은 외부 호출 없이 종료한다. 진행 중인 유효 lease는 건드리지 않는다.
        if (deliveries)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_push_delivery SET status = 'EXPIRED', lease_token = NULL, lease_expires_at_utc = NULL
                WHERE status IN ('SEND','RECEIPT','INVALIDATE')
                  AND COALESCE(receipt_expires_at_utc, TIMESTAMPADD(HOUR, 24, created_at_utc)) <= UTC_TIMESTAMP(6)
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
                """, token);
            return await db.Database.SqlQuery<long>($"""
                SELECT id AS Value FROM chat_push_delivery
                WHERE status IN ('SEND','RECEIPT','INVALIDATE') AND due_at_utc <= UTC_TIMESTAMP(6)
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
                ORDER BY due_at_utc, id LIMIT {options.BatchSize}
                """).ToListAsync(token);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_outbox SET status = 'EXPIRED', lease_token = NULL, lease_expires_at_utc = NULL
            WHERE status = 'PENDING' AND created_at_utc <= TIMESTAMPADD(HOUR, -24, UTC_TIMESTAMP(6))
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return await db.Database.SqlQuery<long>($"""
            SELECT id AS Value FROM chat_push_outbox WHERE status = 'PENDING' AND due_at_utc <= UTC_TIMESTAMP(6)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            ORDER BY due_at_utc, id LIMIT {options.BatchSize}
            """).ToListAsync(token);
    }

    public async Task<ChatPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        string lease = Guid.NewGuid().ToString("D");
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_outbox SET lease_token = {lease},
                lease_expires_at_utc = TIMESTAMPADD(SECOND, {options.LeaseSeconds}, UTC_TIMESTAMP(6)), attempts = attempts + 1
            WHERE id = {id} AND status = 'PENDING' AND due_at_utc <= UTC_TIMESTAMP(6)
              AND created_at_utc > TIMESTAMPADD(HOUR, -24, UTC_TIMESTAMP(6))
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return changed == 0 ? null : await db.ChatPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id && row.LeaseToken == lease, token);
    }

    public async Task<ChatPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        string lease = Guid.NewGuid().ToString("D");
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_delivery SET lease_token = {lease},
                lease_expires_at_utc = TIMESTAMPADD(SECOND, {options.LeaseSeconds}, UTC_TIMESTAMP(6)), attempts = attempts + 1
            WHERE id = {id} AND status IN ('SEND','RECEIPT','INVALIDATE') AND due_at_utc <= UTC_TIMESTAMP(6)
              AND COALESCE(receipt_expires_at_utc, TIMESTAMPADD(HOUR, 24, created_at_utc)) > UTC_TIMESTAMP(6)
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= UTC_TIMESTAMP(6))
            """, token);
        return changed == 0 ? null : await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == id && row.LeaseToken == lease, token);
    }

    public async Task<ChatPushOutboxEntity> OutboxAsync(long id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.ChatPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id, token);
    }

    public async Task<bool> EligibleAsync(ChatPushOutboxEntity job, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);

        // 현재 읽음 cursor, 재가입 시각, 삭제/방 종료/OPEN ban을 실제 조회 규칙으로 다시 확인한다.
        return await (from message in db.ChatMessages
                      join room in db.ChatRooms on message.ChatRoomId equals room.Id
                      join member in db.ChatRoomMembers on room.Id equals member.ChatRoomId
                      where message.Id == job.MessageId && room.Id == job.RoomId
                          && message.SenderUserId == job.SenderUserId && job.SenderUserId != job.RecipientUserId
                          && message.SenderType == "USER" && message.MessageType == "TEXT"
                          && message.Status == "SENT" && message.DeletedAt == null
                          && room.Active && room.DeletedAt == null
                          && (room.RoomType == "DIRECT" || room.RoomType == "GROUP" || room.RoomType == "OPEN")
                          && member.UserId == job.RecipientUserId && member.Active && member.DeletedAt == null
                          && member.JoinedAt <= message.CreatedAt
                          && (member.LastReadMessageId == null || member.LastReadMessageId < message.Id)
                          && (room.RoomType != "OPEN" ||
                              (db.OpenChatRooms.Any(open => open.ChatRoomId == room.Id && open.Status == "ACTIVE")
                               && !db.OpenChatBans.Any(ban => ban.ChatRoomId == room.Id && ban.ReleasedAt == null
                                   && (ban.TargetUserId == job.RecipientUserId || ban.TargetUserId == job.SenderUserId))))
                      select message.Id).AnyAsync(token);
    }

    public async Task<bool> ExpandAsync(ChatPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);

        // 상태 전이와 설치별 유일 행 삽입이 함께 commit된다. 만료/대체 lease의 늦은 결과는 버린다.
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_outbox SET status = 'EXPANDED', lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = 'PENDING' AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token);
        if (changed == 0) return false;

        db.ChatPushDeliveries.AddRange(targets.Select(target => new ChatPushDeliveryEntity
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

    public async Task<bool> FinishOutboxAsync(ChatPushOutboxEntity job, string status, int delaySeconds, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_outbox SET status = {status}, due_at_utc = TIMESTAMPADD(SECOND, {delaySeconds}, UTC_TIMESTAMP(6)),
                lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = 'PENDING' AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token) == 1;
    }

    public async Task<bool> FinishDeliveryAsync(ChatPushDeliveryEntity job, string status, int delaySeconds,
        string? ticketId, bool resetAttempts, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_delivery SET status = {status}, due_at_utc = TIMESTAMPADD(SECOND, {delaySeconds}, UTC_TIMESTAMP(6)),
                ticket_id = {ticketId}, attempts = CASE WHEN {resetAttempts} THEN 0 ELSE attempts END,
                receipt_expires_at_utc = CASE WHEN {job.Status} = 'SEND' AND {status} = 'RECEIPT'
                    THEN TIMESTAMPADD(HOUR, 24, UTC_TIMESTAMP(6)) ELSE receipt_expires_at_utc END,
                lease_token = NULL, lease_expires_at_utc = NULL
            WHERE id = {job.Id} AND status = {job.Status} AND lease_token = {job.LeaseToken}
              AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """, token) == 1;
    }

    public async Task<bool> RefreshTargetAsync(ChatPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token)
    {
        if (!ChatPushProcessor.Matches(job, target)) return false;
        await using var db = await contexts.CreateDbContextAsync(token);

        // 같은 계정/sid binding과 tokenVersion의 resume generation만 전진시킨다.
        // 실제 전송 snapshot을 먼저 영속화하여 늦은 receipt가 이 네 값으로만 무효화하도록 한다.
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_push_delivery SET generation = {target.Generation},
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
    public async Task<bool> OwnsDeliveryAsync(ChatPushDeliveryEntity job, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.ChatPushDeliveries.FromSqlInterpolated($"""
            SELECT * FROM chat_push_delivery WHERE id = {job.Id} AND status = {job.Status}
              AND lease_token = {job.LeaseToken} AND lease_expires_at_utc > UTC_TIMESTAMP(6)
            """).AnyAsync(token);
    }
}
