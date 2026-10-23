using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Safety;

// 재가입 방지용 실제 계정 ID만 내부에 남긴다. 이름/이메일/본문/프로필은 복제하지 않는다.
public sealed class ChatAccountDeletionEntity
{
    public long UserId { get; set; }
    public string OperationId { get; set; } = null!;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? LocalCompletedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class ChatAccountDeletionObjectEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string ObjectKey { get; set; } = null!;
    public int Attempts { get; set; }
    public DateTime DueAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

internal sealed class ChatAccountDeletionConfiguration : IEntityTypeConfiguration<ChatAccountDeletionEntity>
{
    public void Configure(EntityTypeBuilder<ChatAccountDeletionEntity> builder)
    {
        builder.ToTable("chat_account_deletion");
        builder.HasKey(row => row.UserId);
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("bigint").ValueGeneratedNever();
        builder.Property(row => row.OperationId).HasColumnName("operation_id").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        builder.Property(row => row.RequestedAtUtc).HasColumnName("requested_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.LocalCompletedAtUtc).HasColumnName("local_completed_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("datetime(6)");
        builder.HasIndex(row => row.OperationId).IsUnique().HasDatabaseName("uk_chat_account_deletion_operation");
    }
}

internal sealed class ChatAccountDeletionObjectConfiguration : IEntityTypeConfiguration<ChatAccountDeletionObjectEntity>
{
    public void Configure(EntityTypeBuilder<ChatAccountDeletionObjectEntity> builder)
    {
        builder.ToTable("chat_account_deletion_object");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("bigint");
        builder.Property(row => row.ObjectKey).HasColumnName("object_key").HasColumnType("varchar(500)").HasMaxLength(500);
        builder.Property(row => row.Attempts).HasColumnName("attempts").HasColumnType("int");
        builder.Property(row => row.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("datetime(6)");
        builder.HasIndex(row => new { row.UserId, row.ObjectKey }).IsUnique().HasDatabaseName("uk_chat_deletion_object");
        builder.HasIndex(row => new { row.CompletedAtUtc, row.DueAtUtc }).HasDatabaseName("idx_chat_deletion_object_due");
    }
}
