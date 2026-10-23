using System.Data;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Safety;

public sealed class EfChatAccountDeletionStore(
    IDbContextFactory<ChatDbContext> contexts,
    HttpChatCoreClient? core,
    Func<DateTimeOffset, DateTime> localTime,
    TimeProvider time, IOpenProfileUploadJournal? imageJournal = null) : IChatAccountDeletionStore
{
    public async Task<ChatAccountDeletionResult> DeleteAsync(long userId, ChatAccountDeletionRequest request, CancellationToken token)
    {
        ChatAccountDeletionPolicy.Validate(userId, request);
        await using (var db = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            await ChatAccountWriteGate.AcquireAsync(db, [userId], token, deletion: true);
            // gate 대기 중 끝난 이전 쓰기도 포함한다. BE는 전체 정리 완료 전 동일 이메일 재가입을 예약한다.
            var acquiredAt = time.GetUtcNow();
            DateTime now = localTime(acquiredAt);
            DateTime nowUtc = acquiredAt.UtcDateTime;
            // 같은 계정의 반복 호출은 동일 tombstone을 잠근다. UUID 재사용으로 다른 계정을 정리하지 않는다.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT IGNORE INTO chat_account_deletion (user_id,operation_id,requested_at_utc,local_completed_at_utc,completed_at_utc)
                VALUES ({userId},{request.OperationId},{request.RequestedAtUtc.UtcDateTime},NULL,NULL)
                """, token);
            var deletion = await db.ChatAccountDeletions.FromSqlInterpolated(
                $"SELECT * FROM chat_account_deletion WHERE user_id = {userId} FOR UPDATE").SingleOrDefaultAsync(token);
            if (deletion is null || deletion.OperationId != request.OperationId)
                throw new ChatSafetyException("CHAT_ACCOUNT_DELETION_CONFLICT", "탈퇴 정리 작업이 일치하지 않습니다.");

            // 이전 작업은 DB 정리를 반복하지 않는다. 객체 삭제가 남은 요청은 commit 뒤 같은 큐를 이어 간다.
            if (deletion.LocalCompletedAtUtc is not null)
            {
                await transaction.CommitAsync(token);
            }
            else
            {
                await CleanupWithinAsync(db, userId, request, now, now, nowUtc, token);
                deletion.LocalCompletedAtUtc = nowUtc;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
        }

        await ProcessObjectsAsync(userId, token);
        await using var read = await contexts.CreateDbContextAsync(token);
        var result = await read.ChatAccountDeletions.AsNoTracking().SingleAsync(row => row.UserId == userId, token);
        return new(result.OperationId, result.UserId, result.CompletedAtUtc is null ? "PENDING" : "COMPLETED",
            result.CompletedAtUtc is { } completed ? new DateTimeOffset(DateTime.SpecifyKind(completed, DateTimeKind.Utc)) : null);
    }

    private static async Task CleanupWithinAsync(ChatDbContext db, long userId, ChatAccountDeletionRequest request,
        DateTime cutoff, DateTime now, DateTime nowUtc, CancellationToken token)
    {
        // 업로드 부모 transaction과 같은 gate 뒤에 ledger를 잠근다. 불명 PUT도 정리 완료 판단에 포함한다.
        var imageIntents = await db.ChatOpenProfileUploadIntents.FromSqlInterpolated(
            $"SELECT * FROM chat_open_profile_upload_intent WHERE user_id={userId} ORDER BY id FOR UPDATE").ToListAsync(token);
        // 기존 쓰기와 같은 OPEN → room → membership 순서다. 잠금 전에 profile snapshot을 고정하지 않는다.
        await db.OpenChatRooms.FromSqlInterpolated($"""
            SELECT * FROM open_chat_room WHERE chat_room_id IN
              (SELECT id FROM chat_room WHERE owner_id={userId}
                OR id IN (SELECT chat_room_id FROM chat_room_member WHERE user_id={userId}))
            ORDER BY chat_room_id FOR UPDATE
            """).ToListAsync(token);
        var rooms = await db.ChatRooms.FromSqlInterpolated($"""
            SELECT * FROM chat_room WHERE owner_id = {userId}
               OR id IN (SELECT chat_room_id FROM chat_room_member WHERE user_id = {userId}) ORDER BY id FOR UPDATE
            """).ToListAsync(token);
        await db.ChatRoomMembers.FromSqlInterpolated($"SELECT * FROM chat_room_member WHERE user_id={userId} ORDER BY id FOR UPDATE").ToListAsync(token);
        var memberIds = await db.ChatRoomMembers.Where(row => row.UserId == userId).Select(row => row.Id).ToListAsync(token);
        var keys = await db.OpenChatMemberProfiles.Where(row => memberIds.Contains(row.ChatRoomMemberId) && row.ProfileImageObjectKey != null)
            .Select(row => row.ProfileImageObjectKey!).ToListAsync(token);
        keys.AddRange(await db.OpenChatBans.Where(row => row.TargetUserId == userId && row.ProfileImageObjectKeySnapshot != null)
            .Select(row => row.ProfileImageObjectKeySnapshot!).ToListAsync(token));
        foreach (var intent in imageIntents)
        {
            if (keys.Contains(intent.ObjectKey, StringComparer.Ordinal))
            {
                intent.State = "PUT_CONFIRMED";
                intent.PutConfirmedAtUtc ??= nowUtc;
            }
            intent.NextAttemptAtUtc = nowUtc;
        }

        // 소유 방은 관리자/가입 순으로 승계하고, 남은 사람이 없는 방은 종료한다.
        foreach (var room in rooms)
        {
            var successor = await db.ChatRoomMembers.Where(row => row.ChatRoomId == room.Id && row.UserId != userId
                    && row.Active && row.DeletedAt == null && !db.ChatAccountDeletions.Any(gone => gone.UserId == row.UserId))
                .OrderBy(row => row.Role == "ADMIN" ? 0 : 1).ThenBy(row => row.JoinedAt).ThenBy(row => row.Id).FirstOrDefaultAsync(token);
            if (room.OwnerId == userId)
            {
                room.OwnerId = successor?.UserId;
                if (successor is not null) successor.Role = "OWNER";
            }
            if (successor is null)
            {
                room.Active = false;
                var open = await db.OpenChatRooms.SingleOrDefaultAsync(row => row.ChatRoomId == room.Id, token);
                if (open is not null) { open.Status = "CLOSED"; open.ClosedAt ??= now; }
            }
            room.UpdatedAt = now;
            room.UpdatedBy = null;
        }
        await db.SaveChangesAsync(token);

        // 공유 메시지/번역 본문과 ID는 보존하고, 개인 설정·표시·수신 업무만 제거한다.
        await db.ChatRoomMembers.Where(row => row.UserId == userId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Active, false).SetProperty(row => row.LeftAt, row => row.LeftAt ?? now)
            .SetProperty(row => row.DeletedAt, row => row.DeletedAt ?? now)
            .SetProperty(row => row.Role, "MEMBER")
            .SetProperty(row => row.NotificationMuted, true).SetProperty(row => row.CreatedBy, (string?)null)
            .SetProperty(row => row.UpdatedBy, (string?)null), token);
        await db.OpenChatMemberProfiles.Where(row => memberIds.Contains(row.ChatRoomMemberId)).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Nickname, "탈퇴한 사용자").SetProperty(row => row.ProfileImageObjectKey, (string?)null)
            .SetProperty(row => row.CreatedBy, (string?)null).SetProperty(row => row.UpdatedBy, (string?)null), token);
        await db.OpenChatBans.Where(row => row.TargetUserId == userId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.NicknameSnapshot, "탈퇴한 사용자").SetProperty(row => row.ProfileImageObjectKeySnapshot, (string?)null)
            .SetProperty(row => row.CreatedBy, (string?)null).SetProperty(row => row.UpdatedBy, (string?)null), token);
        await db.UserChatLanguageSettings.Where(row => row.UserId == userId).ExecuteDeleteAsync(token);
        await db.ChatNotificationPreferences.Where(row => row.UserId == userId).ExecuteDeleteAsync(token);
        await db.OpenInvitationSenderGates.Where(row => row.ActorUserId == userId).ExecuteDeleteAsync(token);
        await db.ChatNotifications.Where(row => row.RecipientUserId == userId || row.ActorUserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletedAt, now)
                .SetProperty(row => row.PayloadJson, "{}").SetProperty(row => row.ActorUserId, (long?)null)
                .SetProperty(row => row.CreatedBy, (string?)null), token);

        // provider에 이미 넘긴 알림은 회수할 수 없다. 아직 보낼 수 있는 큐와 lease만 무효화한다.
        await SuppressPushAsync(db, userId, token);
        if (!string.IsNullOrWhiteSpace(request.AuditIdentity)) await ScrubAuditAsync(db, request.AuditIdentity, cutoff, token);

        // 다른 사람/AI가 같은 객체를 참조하면 해당 공유 파일을 삭제하지 않는다.
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            bool shared = await db.OpenChatMemberProfiles.AnyAsync(row => row.ProfileImageObjectKey == key, token)
                || await db.OpenChatBans.AnyAsync(row => row.ProfileImageObjectKeySnapshot == key, token)
                || await db.ChatAiAgents.AnyAsync(row => row.ProfileImageObjectKey == key || row.ProfileBackgroundImageObjectKey == key, token);
            if (!shared && !imageIntents.Any(row => row.ObjectKey == key)
                && !await db.ChatAccountDeletionObjects.AnyAsync(row => row.UserId == userId && row.ObjectKey == key, token))
                db.ChatAccountDeletionObjects.Add(new() { UserId = userId, ObjectKey = key, DueAtUtc = nowUtc });
        }
        await db.SaveChangesAsync(token);
    }

    private static async Task SuppressPushAsync(ChatDbContext db, long userId, CancellationToken token)
    {
        foreach (string table in new[] { "chat_push_outbox", "open_invitation_push_outbox" })
        {
            string command = $$"""
                UPDATE `{{table}}` SET status='SUPPRESSED',lease_token=NULL,lease_expires_at_utc=NULL
                WHERE (sender_user_id={0} OR recipient_user_id={0}) AND status IN ('PENDING','EXPANDED')
                """;
            await db.Database.ExecuteSqlRawAsync(command, [userId], token);
        }
        foreach ((string delivery, string outbox) in new[] { ("chat_push_delivery", "chat_push_outbox"), ("open_invitation_push_delivery", "open_invitation_push_outbox") })
        {
            string command = $$"""
                UPDATE `{{delivery}}` AS d INNER JOIN `{{outbox}}` AS o ON d.outbox_id=o.id
                SET d.status='SUPPRESSED',d.lease_token=NULL,d.lease_expires_at_utc=NULL,d.ticket_id=NULL
                WHERE (o.sender_user_id={0} OR o.recipient_user_id={0}) AND d.status IN ('SEND','RECEIPT','INVALIDATE')
                """;
            await db.Database.ExecuteSqlRawAsync(command, [userId], token);
        }
    }

    private static async Task ScrubAuditAsync(ChatDbContext db, string audit, DateTime cutoff, CancellationToken token)
    {
        // 식별자는 고정 allowlist이며 값은 parameter다. BE의 pending 이메일 예약과 gate 획득 시각 cutoff를 함께 적용한다.
        string[] tables = ["chat_room", "chat_room_member", "chat_message", "chat_message_translation", "open_chat_room",
            "open_chat_member_profile", "open_chat_ban", "chat_ai_agent", "chat_room_ai_member", "chat_room_ai_setting",
            "chat_room_ai_activity", "chat_ai_system_setting", "user_chat_language_setting"];
        foreach (string table in tables)
        {
            string createdCommand = $"UPDATE `{table}` SET created_by=NULL WHERE created_by={{0}} AND created_at<={{1}}";
            string updatedCommand = $"UPDATE `{table}` SET updated_by=NULL WHERE updated_by={{0}} AND updated_at<={{1}}";
            await db.Database.ExecuteSqlRawAsync(createdCommand, [audit, cutoff], token);
            await db.Database.ExecuteSqlRawAsync(updatedCommand, [audit, cutoff], token);
        }
        await db.Database.ExecuteSqlRawAsync("UPDATE chat_notification SET created_by=NULL WHERE created_by={0} AND created_at<={1}", [audit, cutoff], token);
    }

    public async Task ProcessObjectsAsync(long? onlyUserId, CancellationToken token)
    {
        if (imageJournal is not null) await imageJournal.ProcessDueAsync(onlyUserId, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.ChatAccountDeletionObjects.Where(row => row.CompletedAtUtc == null
                && (onlyUserId == null || row.UserId == onlyUserId) && row.DueAtUtc <= time.GetUtcNow().UtcDateTime)
            .OrderBy(row => row.Id).Take(50).ToListAsync(token);
        foreach (var row in rows)
        {
            if (core is null) break;
            try
            {
                await core.DeleteAsync(row.ObjectKey, token);
                row.CompletedAtUtc = time.GetUtcNow().UtcDateTime;
                // 정리가 끝난 키 자체도 보관하지 않는다. nullable 대신 행 삭제로 최소 데이터만 남긴다.
                db.ChatAccountDeletionObjects.Remove(row);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                row.Attempts++;
                row.DueAtUtc = time.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(row.Attempts, 6))));
            }
        }
        await db.SaveChangesAsync(token);
        await db.ChatAccountDeletions.Where(row => row.CompletedAtUtc == null && row.LocalCompletedAtUtc != null
            && (onlyUserId == null || row.UserId == onlyUserId)
            && !db.ChatAccountDeletionObjects.Any(item => item.UserId == row.UserId)
            && !db.ChatOpenProfileUploadIntents.Any(item => item.UserId == row.UserId))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.CompletedAtUtc, time.GetUtcNow().UtcDateTime), token);
    }
}
