using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Push;

// outbox에는 본문이나 토큰을 복제하지 않는다. 수신자는 메시지 transaction의 실제 멤버 snapshot이다.
public sealed class ChatPushOutboxEntity
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long RoomId { get; set; }
    public long SenderUserId { get; set; }
    public long RecipientUserId { get; set; }
    public string EventId { get; set; } = null!;
    public string Status { get; set; } = "PENDING";
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
}

// 설치 주소는 BE에서 전송 직전에 다시 조회한다. 영속 보관하는 값은 조건부 무효화에 필요한 버전뿐이다.
public sealed class ChatPushDeliveryEntity
{
    public long Id { get; set; }
    public long OutboxId { get; set; }
    public string InstallationId { get; set; } = null!;
    public string AccountBindingId { get; set; } = null!;
    public long Generation { get; set; }
    public long TokenVersion { get; set; }
    public string Status { get; set; } = "SEND";
    public int Attempts { get; set; }
    public string? TicketId { get; set; }
    public DateTime? ReceiptExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
}

internal sealed class ChatPushOutboxConfiguration : IEntityTypeConfiguration<ChatPushOutboxEntity>
{
    public void Configure(EntityTypeBuilder<ChatPushOutboxEntity> b)
    {
        b.ToTable("chat_push_outbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        b.Property(x => x.MessageId).HasColumnName("message_id").HasColumnType("bigint");
        b.Property(x => x.RoomId).HasColumnName("room_id").HasColumnType("bigint");
        b.Property(x => x.SenderUserId).HasColumnName("sender_user_id").HasColumnType("bigint");
        b.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id").HasColumnType("bigint");
        b.Property(x => x.EventId).HasColumnName("event_id").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(16)").HasMaxLength(16);
        b.Property(x => x.Attempts).HasColumnName("attempts").HasColumnType("int");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("datetime(6)");
        b.Property(x => x.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("datetime(6)");
        b.Property(x => x.LeaseToken).HasColumnName("lease_token").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        b.Property(x => x.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("datetime(6)");
        b.HasIndex(x => new { x.MessageId, x.RecipientUserId }).IsUnique().HasDatabaseName("uk_chat_push_message_recipient");
        b.HasIndex(x => new { x.Status, x.DueAtUtc, x.LeaseExpiresAtUtc, x.Id }).HasDatabaseName("idx_chat_push_outbox_due");
        b.HasOne<TranslaCat.Chat.Infrastructure.Persistence.Entities.ChatMessageEntity>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChatPushDeliveryConfiguration : IEntityTypeConfiguration<ChatPushDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<ChatPushDeliveryEntity> b)
    {
        b.ToTable("chat_push_delivery");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        b.Property(x => x.OutboxId).HasColumnName("outbox_id").HasColumnType("bigint");
        b.Property(x => x.InstallationId).HasColumnName("installation_id").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        b.Property(x => x.AccountBindingId).HasColumnName("account_binding_id").HasColumnType("varchar(128)").HasMaxLength(128).UseCollation("ascii_bin");
        b.Property(x => x.Generation).HasColumnName("generation").HasColumnType("bigint");
        b.Property(x => x.TokenVersion).HasColumnName("token_version").HasColumnType("bigint");
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(16)").HasMaxLength(16);
        b.Property(x => x.Attempts).HasColumnName("attempts").HasColumnType("int");
        b.Property(x => x.TicketId).HasColumnName("ticket_id").HasColumnType("varchar(128)").HasMaxLength(128).UseCollation("ascii_bin");
        b.Property(x => x.ReceiptExpiresAtUtc).HasColumnName("receipt_expires_at_utc").HasColumnType("datetime(6)");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("datetime(6)");
        b.Property(x => x.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("datetime(6)");
        b.Property(x => x.LeaseToken).HasColumnName("lease_token").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        b.Property(x => x.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("datetime(6)");
        b.HasIndex(x => new { x.OutboxId, x.InstallationId }).IsUnique().HasDatabaseName("uk_chat_push_outbox_installation");
        b.HasIndex(x => new { x.Status, x.DueAtUtc, x.LeaseExpiresAtUtc, x.Id }).HasDatabaseName("idx_chat_push_delivery_due");
        b.HasOne<ChatPushOutboxEntity>().WithMany().HasForeignKey(x => x.OutboxId).OnDelete(DeleteBehavior.Restrict);
    }
}
