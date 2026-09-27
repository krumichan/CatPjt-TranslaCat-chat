using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class ChatRoomConfiguration : IEntityTypeConfiguration<ChatRoomEntity>
{
    public void Configure(EntityTypeBuilder<ChatRoomEntity> builder)
    {
        builder.ToTable("chat_room");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.RoomType)
            .HasColumnName("room_type")
            .HasMaxLength(30)
            .HasColumnType("enum('DIRECT','GROUP','OPEN')")
            .IsRequired(true);

        builder.Property(entity => entity.SourceType)
            .HasColumnName("source_type")
            .HasMaxLength(30)
            .HasColumnType("enum('AI','FRIEND','MANUAL','OPEN')")
            .IsRequired(true);

        builder.Property(entity => entity.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .HasColumnType("varchar(100)")
            .IsRequired(false);

        builder.Property(entity => entity.Description)
            .HasColumnName("description")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(false);

        builder.Property(entity => entity.OwnerId)
            .HasColumnName("owner_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.Active)
            .HasColumnName("active")
            .HasColumnType("bit(1)");

        builder.Property(entity => entity.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.OwnerId, "idx_chat_room_owner_id")
            .HasDatabaseName("idx_chat_room_owner_id");

        builder.HasIndex(entity => entity.RoomType, "idx_chat_room_room_type")
            .HasDatabaseName("idx_chat_room_room_type");

        builder.HasIndex(entity => entity.SourceType, "idx_chat_room_source_type")
            .HasDatabaseName("idx_chat_room_source_type");
    }
}
