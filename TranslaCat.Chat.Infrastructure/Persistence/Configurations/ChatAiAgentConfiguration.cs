using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatAiAgentConfiguration : IEntityTypeConfiguration<ChatAiAgentEntity>
{
    public void Configure(EntityTypeBuilder<ChatAiAgentEntity> builder)
    {
        builder.ToTable("chat_ai_agent");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.Nickname)
            .HasColumnName("nickname")
            .HasMaxLength(50)
            .HasColumnType("varchar(50)")
            .IsRequired(true);

        builder.Property(entity => entity.ProfileImageObjectKey)
            .HasColumnName("profile_image_object_key")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(false);

        builder.Property(entity => entity.ProfileBackgroundImageObjectKey)
            .HasColumnName("profile_background_image_object_key")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(false);

        builder.Property(entity => entity.Bio)
            .HasColumnName("bio")
            .HasMaxLength(200)
            .HasColumnType("varchar(200)")
            .IsRequired(false);

        builder.Property(entity => entity.OriginalLanguageCode)
            .HasColumnName("original_language_code")
            .HasMaxLength(10)
            .HasColumnType("varchar(10)")
            .IsRequired(true);

        builder.Property(entity => entity.PersonaPrompt)
            .HasColumnName("persona_prompt")
            .HasColumnType("text")
            .IsRequired(true);

        builder.Property(entity => entity.Active)
            .HasColumnName("active")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);
    }
}
