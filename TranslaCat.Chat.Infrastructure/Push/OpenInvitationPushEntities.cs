using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Push;

// 초대 notification과 같은 transaction에서 저장한다. 본문·닉네임·ID 입력값·토큰은 복제하지 않는다.
public sealed class OpenInvitationPushOutboxEntity
{
    public long Id { get; set; }
    public long NotificationId { get; set; }
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
public sealed class OpenInvitationPushDeliveryEntity
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

internal sealed class OpenInvitationPushOutboxConfiguration : IEntityTypeConfiguration<OpenInvitationPushOutboxEntity>
{
    public void Configure(EntityTypeBuilder<OpenInvitationPushOutboxEntity> b)
    {
        b.ToTable("open_invitation_push_outbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        b.Property(x => x.NotificationId).HasColumnName("notification_id").HasColumnType("bigint");
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
        b.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("uk_open_invitation_push_event");
        b.HasIndex(x => x.NotificationId).IsUnique().HasDatabaseName("uk_open_invitation_push_notification");
        b.HasIndex(x => new { x.SenderUserId, x.CreatedAtUtc }).HasDatabaseName("idx_open_invitation_push_sender_time");
        b.HasIndex(x => new { x.Status, x.DueAtUtc, x.LeaseExpiresAtUtc, x.Id }).HasDatabaseName("idx_open_invitation_push_outbox_due");
        b.HasOne<TranslaCat.Chat.Infrastructure.Persistence.Entities.ChatNotificationEntity>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpenInvitationPushDeliveryConfiguration : IEntityTypeConfiguration<OpenInvitationPushDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<OpenInvitationPushDeliveryEntity> b)
    {
        b.ToTable("open_invitation_push_delivery");
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
        b.HasIndex(x => new { x.OutboxId, x.InstallationId }).IsUnique().HasDatabaseName("uk_open_invitation_push_outbox_installation");
        b.HasIndex(x => new { x.Status, x.DueAtUtc, x.LeaseExpiresAtUtc, x.Id }).HasDatabaseName("idx_open_invitation_push_delivery_due");
        b.HasOne<OpenInvitationPushOutboxEntity>().WithMany().HasForeignKey(x => x.OutboxId).OnDelete(DeleteBehavior.Restrict);
    }
}
