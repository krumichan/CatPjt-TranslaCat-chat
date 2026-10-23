using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Safety;

public sealed class ChatAccountWriteGateEntity { public long UserId { get; set; } }
internal sealed class ChatAccountWriteGateConfiguration : IEntityTypeConfiguration<ChatAccountWriteGateEntity>
{
    public void Configure(EntityTypeBuilder<ChatAccountWriteGateEntity> builder)
    {
        builder.ToTable("chat_account_write_gate");
        builder.HasKey(row => row.UserId);
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("bigint").ValueGeneratedNever();
    }
}

public static class ChatAccountWriteGate
{
    public static async Task AcquireAsync(ChatDbContext db, IEnumerable<long> accounts, CancellationToken token, bool deletion = false)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("CHAT account gate requires an explicit transaction.");
        if (db.SafetyGateTransactionId != db.Database.CurrentTransaction.TransactionId)
        {
            // pooled DbContext의 사용자 필드는 자동 초기화되지 않는다. 잠금 증명은 현재 transaction에만 유효하다.
            db.LockedSafetyAccounts.Clear();
            db.SafetyGateTransactionId = db.Database.CurrentTransaction.TransactionId;
        }
        long[] ids = accounts.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length > 2000) throw new ChatSafetyException("CHAT_SAFETY_INVALID", "대상 계정 수를 확인해 주세요.");

        // 계정 gate는 항상 오름차순으로, OPEN/room/member 잠금 전에 획득한다. DB transaction 종료까지 유지된다.
        foreach (long id in ids)
        {
            // duplicate INSERT IGNORE의 shared-lock 승격은 여러 대기자가 있을 때 deadlock을 만든다.
            // no-op upsert가 처음부터 배타 잠금을 얻어 같은 계정의 후속 transaction을 직렬화한다.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO chat_account_write_gate (user_id) VALUES ({id}) ON DUPLICATE KEY UPDATE user_id=user_id", token);
            _ = await db.ChatAccountWriteGates.FromSqlInterpolated($"SELECT * FROM chat_account_write_gate WHERE user_id={id} FOR UPDATE").SingleAsync(token);
            db.LockedSafetyAccounts.Add(id);
        }
        if (deletion) return;

        // snapshot이 아닌 locking current read다. gate 대기 중 commit된 탈퇴도 새 쓰기 전에 거절한다.
        foreach (long id in ids)
        {
            if ((await db.ChatAccountDeletions.FromSqlInterpolated($"SELECT * FROM chat_account_deletion WHERE user_id={id} FOR UPDATE").ToListAsync(token)).Count > 0)
                throw new ChatSafetyException("CHAT_ACCOUNT_DELETED", "탈퇴한 계정의 데이터는 다시 저장할 수 없습니다.");
        }
    }

    public static void RequireHeld(ChatDbContext db, long userId)
    {
        if (db.Database.CurrentTransaction is null || db.SafetyGateTransactionId != db.Database.CurrentTransaction.TransactionId
            || !db.LockedSafetyAccounts.Contains(userId))
            throw new ChatSafetyException("CHAT_ACCOUNT_FENCE_CHANGED", "계정 상태가 변경되었습니다. 다시 확인해 주세요.");
    }
}
