using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatAiSystemSettingConfiguration : IEntityTypeConfiguration<ChatAiSystemSettingEntity>
{
    public void Configure(EntityTypeBuilder<ChatAiSystemSettingEntity> builder)
    {
        builder.ToTable("chat_ai_system_setting");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasMaxLength(30)
            .HasColumnType("varchar(30)")
            .IsRequired(true)
            .ValueGeneratedNever();

        builder.Property(entity => entity.MaxAiMembersPerRoom)
            .HasColumnName("max_ai_members_per_room")
            .HasColumnType("int");

        builder.Property(entity => entity.ConversationResponseRate)
            .HasColumnName("conversation_response_rate")
            .HasColumnType("int");

        builder.Property(entity => entity.ConversationCooldownSeconds)
            .HasColumnName("conversation_cooldown_seconds")
            .HasColumnType("int");

        builder.Property(entity => entity.ConversationMinHumanMessagesAfterAi)
            .HasColumnName("conversation_min_human_messages_after_ai")
            .HasColumnType("int");

        builder.Property(entity => entity.ResponseDelayEnabled)
            .HasColumnName("response_delay_enabled")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.ResponseDelayMinMillis)
            .HasColumnName("response_delay_min_millis")
            .HasColumnType("int");

        builder.Property(entity => entity.ResponseDelayMaxMillis)
            .HasColumnName("response_delay_max_millis")
            .HasColumnType("int");

        builder.Property(entity => entity.RevivalFirstDelayHours)
            .HasColumnName("revival_first_delay_hours")
            .HasColumnType("int");

        builder.Property(entity => entity.RevivalSecondDelayHours)
            .HasColumnName("revival_second_delay_hours")
            .HasColumnType("int");

        builder.Property(entity => entity.RevivalThirdDelayHours)
            .HasColumnName("revival_third_delay_hours")
            .HasColumnType("int");

        builder.Property(entity => entity.RevivalAllowedStartTime)
            .HasColumnName("revival_allowed_start_time")
            .HasColumnType("time(6)");

        builder.Property(entity => entity.RevivalAllowedEndTime)
            .HasColumnName("revival_allowed_end_time")
            .HasColumnType("time(6)");

        builder.Property(entity => entity.ContextMaxMessages)
            .HasColumnName("context_max_messages")
            .HasColumnType("int");

        builder.Property(entity => entity.ContextMaxCharacters)
            .HasColumnName("context_max_characters")
            .HasColumnType("int");

        builder.Property(entity => entity.ReplyMaxCharacters)
            .HasColumnName("reply_max_characters")
            .HasColumnType("int");

        builder.Property(entity => entity.MentionRateLimitCount)
            .HasColumnName("mention_rate_limit_count")
            .HasColumnType("int");

        builder.Property(entity => entity.MentionRateLimitWindowSeconds)
            .HasColumnName("mention_rate_limit_window_seconds")
            .HasColumnType("int");

        ChatAuditConfiguration.Configure(builder, true);
    }
}
