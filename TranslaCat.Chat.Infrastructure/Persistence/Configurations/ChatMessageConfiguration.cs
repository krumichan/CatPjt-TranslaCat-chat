using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessageEntity>
{
    public void Configure(EntityTypeBuilder<ChatMessageEntity> builder)
    {
        builder.ToTable("chat_message");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.SenderUserId)
            .HasColumnName("sender_user_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.SenderAiMemberId)
            .HasColumnName("sender_ai_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.SenderType)
            .HasColumnName("sender_type")
            .HasMaxLength(30)
            .HasColumnType("enum('AI','SYSTEM','USER')")
            .IsRequired(true);

        builder.Property(entity => entity.MessageType)
            .HasColumnName("message_type")
            .HasMaxLength(30)
            .HasColumnType("enum('SYSTEM','TEXT')")
            .IsRequired(true);

        builder.Property(entity => entity.Content)
            .HasColumnName("content")
            .HasColumnType("text")
            .IsRequired(true);

        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasColumnType("enum('DELETED','SENT')")
            .IsRequired(true);

        builder.Property(entity => entity.AiRequestId)
            .HasColumnName("ai_request_id")
            .HasMaxLength(100)
            .HasColumnType("varchar(100)")
            .IsRequired(false);

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.AiRequestId, "uk_chat_message_ai_request_id")
            .HasDatabaseName("uk_chat_message_ai_request_id")
            .IsUnique();

        builder.HasIndex(entity => new { entity.ChatRoomId, entity.CreatedAt, entity.Id }, "idx_chat_message_room_created_id")
            .HasDatabaseName("idx_chat_message_room_created_id");

        builder.HasIndex(entity => new { entity.ChatRoomId, entity.Id }, "idx_chat_message_room_id_id")
            .HasDatabaseName("idx_chat_message_room_id_id");

        builder.HasIndex(entity => entity.SenderAiMemberId, "idx_chat_message_sender_ai_member_id")
            .HasDatabaseName("idx_chat_message_sender_ai_member_id");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ChatRoomAiMemberEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.SenderAiMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
