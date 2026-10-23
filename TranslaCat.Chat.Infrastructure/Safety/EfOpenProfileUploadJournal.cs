using System.Data;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Safety;

public sealed class EfOpenProfileUploadJournal(IDbContextFactory<ChatDbContext> contexts,
    HttpChatCoreClient? core, TimeProvider time) : IOpenProfileUploadJournal
{
    public async Task StartAsync(long userId, long memberId, string newKey, string? previousKey, CancellationToken token)
    {
        Validate(userId, memberId, newKey, ownMember: true);
        if (previousKey is not null) Validate(userId, memberId, previousKey);

        // 부모가 가진 user gate를 여기서 다시 얻지 않는다. FK도 없으므로 부모 row lock과 역순 대기하지 않는다.
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (previousKey is not null) await TrackExistingWithinAsync(db, userId, memberId, previousKey, token);
        db.ChatOpenProfileUploadIntents.Add(new()
        {
            UserId = userId, MemberId = memberId, ObjectKey = newKey,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime, NextAttemptAtUtc = time.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
    }

    public async Task TrackExistingAsync(long userId, long memberId, string key, CancellationToken token)
    {
        Validate(userId, memberId, key);
        await using var db = await contexts.CreateDbContextAsync(token);
        await TrackExistingWithinAsync(db, userId, memberId, key, token);
    }

    private async Task TrackExistingWithinAsync(ChatDbContext db, long userId, long memberId, string key, CancellationToken token)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // 현재 DB 참조는 이전 PUT가 완료되었다는 양의 증거다. 교체/삭제 전에 이전 키도 독립 commit한다.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_open_profile_upload_intent
              (user_id,member_id,object_key,state,created_at_utc,put_confirmed_at_utc,next_attempt_at_utc,attempts)
            VALUES ({userId},{memberId},{key},'PUT_CONFIRMED',{now},{now},{now},0)
            ON DUPLICATE KEY UPDATE state='PUT_CONFIRMED',put_confirmed_at_utc=COALESCE(put_confirmed_at_utc,{now}),next_attempt_at_utc={now}
            """, token);
    }

    public Task ConfirmPutAsync(long userId, string key, CancellationToken token) => MarkAsync(userId, key, true, token);
    public Task MarkUncertainAsync(long userId, string key, CancellationToken token) => MarkAsync(userId, key, false, token);

    private async Task MarkAsync(long userId, string key, bool confirmed, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = time.GetUtcNow().UtcDateTime;
        // 늦은 ACK는 기존 intent만 갱신한다. 탈퇴 뒤 개인 profile/membership을 다시 생성하지 않는다.
        int changed = confirmed
            ? await db.ChatOpenProfileUploadIntents.Where(row => row.UserId == userId && row.ObjectKey == key)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, "PUT_CONFIRMED")
                    .SetProperty(row => row.PutConfirmedAtUtc, now).SetProperty(row => row.NextAttemptAtUtc, now), token)
            : await db.ChatOpenProfileUploadIntents.Where(row => row.UserId == userId && row.ObjectKey == key && row.PutConfirmedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, "PUT_UNCERTAIN")
                    .SetProperty(row => row.NextAttemptAtUtc, now), token);
        if (confirmed && changed != 1) throw new InvalidOperationException("OPEN image intent acknowledgement is missing.");
    }

    public async Task ProcessDueAsync(long? userId, CancellationToken token)
    {
        await using var scan = await contexts.CreateDbContextAsync(token);
        var due = await scan.ChatOpenProfileUploadIntents.AsNoTracking()
            .Where(row => (userId == null || row.UserId == userId) && row.NextAttemptAtUtc <= time.GetUtcNow().UtcDateTime)
            .OrderBy(row => row.Id).Select(row => new { row.Id, row.UserId }).Take(50).ToListAsync(token);
        foreach (var candidate in due)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            // gate → ledger → 참조 순서를 지킨다. PUT ACK 직후 아직 commit 안 된 유효 이미지를 먼저 지우지 않는다.
            await ChatAccountWriteGate.AcquireAsync(db, [candidate.UserId], token, deletion: true);
            var row = await db.ChatOpenProfileUploadIntents.FromSqlInterpolated(
                $"SELECT * FROM chat_open_profile_upload_intent WHERE id={candidate.Id} FOR UPDATE").SingleOrDefaultAsync(token);
            if (row is null || row.NextAttemptAtUtc is null || row.NextAttemptAtUtc > time.GetUtcNow().UtcDateTime) continue;

            bool referenced = await IsReferencedAsync(db, row.ObjectKey, token);
            bool deleted = await db.ChatAccountDeletions.AnyAsync(value => value.UserId == row.UserId, token);
            if (referenced)
            {
                // 공유 AI avatar/다른 사람 참조는 개인 삭제 대상이 아니다. 활성 개인 참조는 다음 교체까지 보존한다.
                if (deleted) db.ChatOpenProfileUploadIntents.Remove(row);
                else
                {
                    row.State = "PUT_CONFIRMED";
                    row.PutConfirmedAtUtc ??= time.GetUtcNow().UtcDateTime;
                    row.NextAttemptAtUtc = null;
                }
            }
            else if (row.PutConfirmedAtUtc is null)
            {
                // DELETE ACK/시간 경과는 진행 중인 PUT의 종료 증거가 아니다. 늦은 PUT ACK 또는 운영 증거까지 PENDING이다.
                row.State = "PUT_UNCERTAIN";
                row.NextAttemptAtUtc = null;
            }
            else if (core is not null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await core.DeleteAsync(row.ObjectKey, timeout.Token);
                    db.ChatOpenProfileUploadIntents.Remove(row);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    row.Attempts++;
                    row.NextAttemptAtUtc = time.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(row.Attempts, 6))));
                }
            }
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
    }

    internal static async Task<bool> IsReferencedAsync(ChatDbContext db, string key, CancellationToken token)
        => await db.OpenChatMemberProfiles.AnyAsync(row => row.ProfileImageObjectKey == key, token)
            || await db.OpenChatBans.AnyAsync(row => row.ProfileImageObjectKeySnapshot == key, token)
            || await db.ChatAiAgents.AnyAsync(row => row.ProfileImageObjectKey == key || row.ProfileBackgroundImageObjectKey == key, token);

    private static void Validate(long userId, long memberId, string key, bool ownMember = false)
    {
        if (userId <= 0 || memberId <= 0 || key != OpenRoomPolicy.NormalizeObjectKey(key, ownMember ? memberId : null))
            throw new ArgumentException("Invalid personal OPEN image intent.");
    }
}
