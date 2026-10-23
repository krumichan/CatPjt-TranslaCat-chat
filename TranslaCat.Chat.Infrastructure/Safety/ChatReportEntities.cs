using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Safety;

// 원문 증거는 일반 채팅 DTO와 분리한다. 만료 뒤 identity/본문/자유 서술을 지운다.
public sealed class ChatReportEntity
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long? ReporterUserId { get; set; }
    public string ClientRequestId { get; set; } = null!;
    public string RequestFingerprint { get; set; } = null!;
    public string TargetType { get; set; } = null!;
    public long? MessageId { get; set; }
    public long? TargetUserId { get; set; }
    public long? TargetMemberId { get; set; }
    public string Reason { get; set; } = null!;
    public string? Detail { get; set; }
    public string? EvidenceContent { get; set; }
    public string? EvidenceSenderDisplay { get; set; }
    public string Status { get; set; } = "PENDING";
    public string? ReviewNote { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime EvidenceExpiresAt { get; set; }
    public DateTime? EvidencePurgedAt { get; set; }
}

public sealed class ChatReportReviewEntity
{
    public long Id { get; set; }
    public long ReportId { get; set; }
    public long? ReviewerUserId { get; set; }
    public string PreviousStatus { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class ChatReportConfiguration : IEntityTypeConfiguration<ChatReportEntity>
{
    public void Configure(EntityTypeBuilder<ChatReportEntity> builder)
    {
        builder.ToTable("chat_report");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        builder.Property(row => row.RoomId).HasColumnName("room_id").HasColumnType("bigint");
        builder.Property(row => row.ReporterUserId).HasColumnName("reporter_user_id").HasColumnType("bigint");
        builder.Property(row => row.ClientRequestId).HasColumnName("client_request_id").HasColumnType("varchar(36)").HasMaxLength(36).UseCollation("ascii_bin");
        builder.Property(row => row.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("ascii_bin");
        builder.Property(row => row.TargetType).HasColumnName("target_type").HasColumnType("varchar(16)").HasMaxLength(16);
        builder.Property(row => row.MessageId).HasColumnName("message_id").HasColumnType("bigint");
        builder.Property(row => row.TargetUserId).HasColumnName("target_user_id").HasColumnType("bigint");
        builder.Property(row => row.TargetMemberId).HasColumnName("target_member_id").HasColumnType("bigint");
        builder.Property(row => row.Reason).HasColumnName("reason").HasColumnType("varchar(32)").HasMaxLength(32);
        builder.Property(row => row.Detail).HasColumnName("detail").HasColumnType("text");
        builder.Property(row => row.EvidenceContent).HasColumnName("evidence_content").HasColumnType("text");
        builder.Property(row => row.EvidenceSenderDisplay).HasColumnName("evidence_sender_display").HasColumnType("varchar(150)").HasMaxLength(150);
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("varchar(16)").HasMaxLength(16);
        builder.Property(row => row.ReviewNote).HasColumnName("review_note").HasColumnType("text");
        builder.Property(row => row.Version).HasColumnName("version").HasColumnType("int").IsConcurrencyToken();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(6)");
        builder.Property(row => row.EvidenceExpiresAt).HasColumnName("evidence_expires_at").HasColumnType("datetime(6)");
        builder.Property(row => row.EvidencePurgedAt).HasColumnName("evidence_purged_at").HasColumnType("datetime(6)");
        builder.HasIndex(row => new { row.ReporterUserId, row.ClientRequestId }).IsUnique().HasDatabaseName("uk_chat_report_request");
        builder.HasIndex(row => new { row.Status, row.Id }).HasDatabaseName("idx_chat_report_status_id");
        builder.HasIndex(row => new { row.EvidencePurgedAt, row.EvidenceExpiresAt }).HasDatabaseName("idx_chat_report_evidence_due");
    }
}

internal sealed class ChatReportReviewConfiguration : IEntityTypeConfiguration<ChatReportReviewEntity>
{
    public void Configure(EntityTypeBuilder<ChatReportReviewEntity> builder)
    {
        builder.ToTable("chat_report_review");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("bigint").ValueGeneratedOnAdd();
        builder.Property(row => row.ReportId).HasColumnName("report_id").HasColumnType("bigint");
        builder.Property(row => row.ReviewerUserId).HasColumnName("reviewer_user_id").HasColumnType("bigint");
        builder.Property(row => row.PreviousStatus).HasColumnName("previous_status").HasColumnType("varchar(16)").HasMaxLength(16);
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("varchar(16)").HasMaxLength(16);
        builder.Property(row => row.Note).HasColumnName("note").HasColumnType("text");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        builder.HasOne<ChatReportEntity>().WithMany().HasForeignKey(row => row.ReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(row => new { row.ReportId, row.Id }).HasDatabaseName("idx_chat_report_review_history");
    }
}
