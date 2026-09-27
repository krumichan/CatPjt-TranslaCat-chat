using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatRoomAiActivityConfiguration : IEntityTypeConfiguration<ChatRoomAiActivityEntity>
{
    public void Configure(EntityTypeBuilder<ChatRoomAiActivityEntity> builder)
    {
        builder.ToTable("chat_room_ai_activity");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");
        builder.Property(entity => entity.ChatRoomId)
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.Property(entity => entity.LastHumanMessageId)
            .HasColumnName("last_human_message_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.LastHumanMessageAt)
            .HasColumnName("last_human_message_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.RevivalCycleVersion)
            .HasColumnName("revival_cycle_version")
            .HasColumnType("bigint");

        builder.Property(entity => entity.RevivalStage)
            .HasColumnName("revival_stage")
            .HasColumnType("int");

        builder.Property(entity => entity.LastRevivalAt)
            .HasColumnName("last_revival_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.NextRevivalAt)
            .HasColumnName("next_revival_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.RevivalStopped)
            .HasColumnName("revival_stopped")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.LastRevivalAiMemberId)
            .HasColumnName("last_revival_ai_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.ClaimToken)
            .HasColumnName("claim_token")
            .HasMaxLength(36)
            .HasColumnType("varchar(36)")
            .IsRequired(false);

        builder.Property(entity => entity.ClaimExpiresAt)
            .HasColumnName("claim_expires_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.ChatRoomId, "uk_chat_room_ai_activity_room")
            .HasDatabaseName("uk_chat_room_ai_activity_room")
            .IsUnique();

        builder.HasIndex(entity => new { entity.RevivalStopped, entity.NextRevivalAt }, "idx_chat_room_ai_activity_revival_due")
            .HasDatabaseName("idx_chat_room_ai_activity_revival_due");

        builder.HasIndex(entity => entity.ClaimExpiresAt, "idx_chat_room_ai_activity_claim")
            .HasDatabaseName("idx_chat_room_ai_activity_claim");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithOne()
            .HasForeignKey<ChatRoomAiActivityEntity>(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
