using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Safety;

// 외부 PUT와 CHAT transaction 사이에 생길 수 있는 미확정 객체를 추적한다. 계정/멤버 FK는 의도적으로 없다.
public sealed class ChatOpenProfileUploadIntentEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long MemberId { get; set; }
    public string ObjectKey { get; set; } = null!;
    public string State { get; set; } = "PUT_STARTED";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PutConfirmedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
}

internal sealed class ChatOpenProfileUploadIntentConfiguration : IEntityTypeConfiguration<ChatOpenProfileUploadIntentEntity>
{
    public void Configure(EntityTypeBuilder<ChatOpenProfileUploadIntentEntity> builder)
    {
        builder.ToTable("chat_open_profile_upload_intent");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("bigint");
        builder.Property(row => row.MemberId).HasColumnName("member_id").HasColumnType("bigint");
        builder.Property(row => row.ObjectKey).HasColumnName("object_key").HasColumnType("varchar(500)").HasMaxLength(500).UseCollation("utf8mb4_bin");
        builder.Property(row => row.State).HasColumnName("state").HasColumnType("varchar(20)").HasMaxLength(20).UseCollation("ascii_bin");
        builder.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.PutConfirmedAtUtc).HasColumnName("put_confirmed_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc").HasColumnType("datetime(6)");
        builder.Property(row => row.Attempts).HasColumnName("attempts").HasColumnType("int");
        builder.HasIndex(row => new { row.UserId, row.ObjectKey }).IsUnique().HasDatabaseName("uk_chat_open_profile_upload_key");
        builder.HasIndex(row => row.NextAttemptAtUtc).HasDatabaseName("idx_chat_open_profile_upload_due");
    }
}
