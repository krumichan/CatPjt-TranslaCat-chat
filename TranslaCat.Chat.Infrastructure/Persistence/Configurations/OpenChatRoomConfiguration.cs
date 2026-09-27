using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class OpenChatRoomConfiguration : IEntityTypeConfiguration<OpenChatRoomEntity>
{
    public void Configure(EntityTypeBuilder<OpenChatRoomEntity> builder)
    {
        builder.ToTable("open_chat_room");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.Visibility)
            .HasColumnName("visibility")
            .HasMaxLength(20)
            .HasColumnType("enum('PUBLIC','UNLISTED')")
            .IsRequired(true);

        builder.Property(entity => entity.MaxMemberCount)
            .HasColumnName("max_member_count")
            .HasColumnType("int");

        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasColumnType("enum('ACTIVE','CLOSED')")
            .IsRequired(true);

        builder.Property(entity => entity.ClosedAt)
            .HasColumnName("closed_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.ChatRoomId, "uk_open_chat_room_chat_room")
            .HasDatabaseName("uk_open_chat_room_chat_room")
            .IsUnique();

        builder.HasIndex(entity => new { entity.Visibility, entity.Status }, "idx_open_chat_room_visibility_status")
            .HasDatabaseName("idx_open_chat_room_visibility_status");

        builder.HasIndex(entity => entity.Status, "idx_open_chat_room_status")
            .HasDatabaseName("idx_open_chat_room_status");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithOne()
            .HasForeignKey<OpenChatRoomEntity>(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
