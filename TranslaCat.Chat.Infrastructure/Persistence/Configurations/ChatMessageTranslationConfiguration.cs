using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatMessageTranslationConfiguration : IEntityTypeConfiguration<ChatMessageTranslationEntity>
{
    public void Configure(EntityTypeBuilder<ChatMessageTranslationEntity> builder)
    {
        builder.ToTable("chat_message_translation");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatMessageId)
            .HasColumnName("chat_message_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.LanguageCode)
            .HasColumnName("language_code")
            .HasMaxLength(10)
            .HasColumnType("varchar(10)")
            .IsRequired(true);

        builder.Property(entity => entity.TranslatedContent)
            .HasColumnName("translated_content")
            .HasColumnType("text")
            .IsRequired(false);

        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasColumnType("enum('COMPLETED','FAILED','PENDING')")
            .IsRequired(true);

        builder.Property(entity => entity.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000)
            .HasColumnType("varchar(1000)")
            .IsRequired(false);

        builder.Property(entity => entity.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 새 CHAT 실행 소유권이다. 기존 비즈니스 status/응답에는 처리 token을 노출하지 않는다.
        builder.Property(entity => entity.ProcessingToken)
            .HasColumnName("processing_token")
            .HasColumnType("varchar(36)")
            .HasMaxLength(36);

        builder.Property(entity => entity.ProcessingExpiresAt)
            .HasColumnName("processing_expires_at")
            .HasColumnType("datetime(6)");

        builder.HasIndex(entity => new { entity.Status, entity.DeletedAt, entity.ProcessingExpiresAt, entity.Id },
                "idx_chat_translation_processing_due")
            .HasDatabaseName("idx_chat_translation_processing_due");

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => new { entity.ChatMessageId, entity.LanguageCode }, "uk_chat_message_translation_message_language")
            .HasDatabaseName("uk_chat_message_translation_message_language")
            .IsUnique();

        builder.HasIndex(entity => entity.ChatMessageId, "idx_chat_translation_message_id")
            .HasDatabaseName("idx_chat_translation_message_id");

        builder.HasIndex(entity => entity.Status, "idx_chat_translation_status")
            .HasDatabaseName("idx_chat_translation_status");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatMessageEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ChatMessageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
