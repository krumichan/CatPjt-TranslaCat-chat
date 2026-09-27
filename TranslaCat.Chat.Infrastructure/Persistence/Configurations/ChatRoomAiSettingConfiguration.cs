using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatRoomAiSettingConfiguration : IEntityTypeConfiguration<ChatRoomAiSettingEntity>
{
    public void Configure(EntityTypeBuilder<ChatRoomAiSettingEntity> builder)
    {
        builder.ToTable("chat_room_ai_setting");
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

        builder.Property(entity => entity.DisclosureType)
            .HasColumnName("disclosure_type")
            .HasMaxLength(20)
            .HasColumnType("enum('PRIVATE','PUBLIC')")
            .IsRequired(true);

        builder.Property(entity => entity.MentionPermission)
            .HasColumnName("mention_permission")
            .HasMaxLength(30)
            .HasColumnType("enum('ALL_MEMBERS','OWNER_ADMIN_ONLY')")
            .IsRequired(true);

        builder.Property(entity => entity.ConversationEnabled)
            .HasColumnName("conversation_enabled")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.RevivalEnabled)
            .HasColumnName("revival_enabled")
            .HasColumnType("bit(1)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.ChatRoomId, "uk_chat_room_ai_setting_room")
            .HasDatabaseName("uk_chat_room_ai_setting_room")
            .IsUnique();

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithOne()
            .HasForeignKey<ChatRoomAiSettingEntity>(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
