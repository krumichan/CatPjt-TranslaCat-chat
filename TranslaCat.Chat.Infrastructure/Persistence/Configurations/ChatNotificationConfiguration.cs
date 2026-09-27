using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatNotificationConfiguration : IEntityTypeConfiguration<ChatNotificationEntity>
{
    public void Configure(EntityTypeBuilder<ChatNotificationEntity> builder)
    {
        builder.ToTable("chat_notification");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.RecipientUserId)
            .HasColumnName("recipient_user_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.NotificationType)
            .HasColumnName("notification_type")
            .HasMaxLength(50)
            .HasColumnType("enum('CHAT_INVITATION','OPEN_CHAT_KICKED','OPEN_CHAT_ROLE_CHANGED','OPEN_CHAT_ROOM_CLOSED')")
            .IsRequired(true);

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.ActorUserId)
            .HasColumnName("actor_user_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("text")
            .IsRequired(true);

        builder.Property(entity => entity.SourceEventKey)
            .HasColumnName("source_event_key")
            .HasMaxLength(160)
            .HasColumnType("varchar(160)")
            .IsRequired(true);

        builder.Property(entity => entity.IsRead)
            .HasColumnName("is_read")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.ReadAt)
            .HasColumnName("read_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, false);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => new { entity.RecipientUserId, entity.IsRead, entity.Id }, "idx_chat_notification_recipient_read_id")
            .HasDatabaseName("idx_chat_notification_recipient_read_id");

        builder.HasIndex(entity => new { entity.RecipientUserId, entity.CreatedAt }, "idx_chat_notification_recipient_created")
            .HasDatabaseName("idx_chat_notification_recipient_created");

        builder.HasIndex(entity => entity.ChatRoomId, "idx_chat_notification_room_id")
            .HasDatabaseName("idx_chat_notification_room_id");

        builder.HasIndex(entity => new { entity.RecipientUserId, entity.NotificationType, entity.SourceEventKey }, "uk_chat_notification_recipient_type_source")
            .HasDatabaseName("uk_chat_notification_recipient_type_source")
            .IsUnique();

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
