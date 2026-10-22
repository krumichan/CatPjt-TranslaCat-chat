using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatNotificationPreferenceConfiguration : IEntityTypeConfiguration<ChatNotificationPreferenceEntity>
{
    public void Configure(EntityTypeBuilder<ChatNotificationPreferenceEntity> builder)
    {
        builder.ToTable("chat_notification_preference");
        builder.HasKey(row => row.UserId);
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("bigint").ValueGeneratedNever();
        builder.Property(row => row.PushEnabled).HasColumnName("push_enabled").HasColumnType("bit(1)");
        builder.Property(row => row.EnabledSinceUtc).HasColumnName("enabled_since_utc").HasColumnType("datetime(6)");
    }
}
