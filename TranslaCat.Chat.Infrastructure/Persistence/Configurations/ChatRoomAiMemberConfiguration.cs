using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatRoomAiMemberConfiguration : IEntityTypeConfiguration<ChatRoomAiMemberEntity>
{
    public void Configure(EntityTypeBuilder<ChatRoomAiMemberEntity> builder)
    {
        builder.ToTable("chat_room_ai_member");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.AiAgentId)
            .HasColumnName("ai_agent_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.Active)
            .HasColumnName("active")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.JoinedAt)
            .HasColumnName("joined_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.LeftAt)
            .HasColumnName("left_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => new { entity.ChatRoomId, entity.AiAgentId }, "uk_chat_room_ai_member_room_agent")
            .HasDatabaseName("uk_chat_room_ai_member_room_agent")
            .IsUnique();

        builder.HasIndex(entity => new { entity.ChatRoomId, entity.Active }, "idx_chat_room_ai_member_room_active")
            .HasDatabaseName("idx_chat_room_ai_member_room_active");

        builder.HasIndex(entity => new { entity.AiAgentId, entity.Active }, "idx_chat_room_ai_member_agent_active")
            .HasDatabaseName("idx_chat_room_ai_member_agent_active");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ChatAiAgentEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.AiAgentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
