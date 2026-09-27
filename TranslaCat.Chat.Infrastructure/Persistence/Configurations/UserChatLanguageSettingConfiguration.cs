using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class UserChatLanguageSettingConfiguration : IEntityTypeConfiguration<UserChatLanguageSettingEntity>
{
    public void Configure(EntityTypeBuilder<UserChatLanguageSettingEntity> builder)
    {
        builder.ToTable("user_chat_language_setting");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.UserId)
            .HasColumnName("user_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.OriginalLanguageCode)
            .HasColumnName("original_language_code")
            .HasMaxLength(10)
            .HasColumnType("varchar(10)")
            .IsRequired(true);

        builder.Property(entity => entity.TranslationLanguageCode)
            .HasColumnName("translation_language_code")
            .HasMaxLength(10)
            .HasColumnType("varchar(10)")
            .IsRequired(true);

        builder.Property(entity => entity.ShowOriginal)
            .HasColumnName("show_original")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.ShowTranslation)
            .HasColumnName("show_translation")
            .HasColumnType("bit(1)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.UserId, "uk_user_chat_language_setting_user")
            .HasDatabaseName("uk_user_chat_language_setting_user")
            .IsUnique();

        builder.HasIndex(entity => entity.UserId, "idx_user_chat_language_setting_user")
            .HasDatabaseName("idx_user_chat_language_setting_user");
    }
}
