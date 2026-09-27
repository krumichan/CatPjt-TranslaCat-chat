using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class OpenChatBanConfiguration : IEntityTypeConfiguration<OpenChatBanEntity>
{
    public void Configure(EntityTypeBuilder<OpenChatBanEntity> builder)
    {
        builder.ToTable("open_chat_ban");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomId)
            .HasColumnName("chat_room_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.TargetUserId)
            .HasColumnName("target_user_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.TargetChatRoomMemberId)
            .HasColumnName("target_chat_room_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.TargetMemberCode)
            .HasColumnName("target_member_code")
            .HasMaxLength(20)
            .HasColumnType("varchar(20)")
            .IsRequired(true);

        builder.Property(entity => entity.NicknameSnapshot)
            .HasColumnName("nickname_snapshot")
            .HasMaxLength(50)
            .HasColumnType("varchar(50)")
            .IsRequired(true);

        builder.Property(entity => entity.ProfileImageObjectKeySnapshot)
            .HasColumnName("profile_image_object_key_snapshot")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(false);

        builder.Property(entity => entity.LastJoinedAtSnapshot)
            .HasColumnName("last_joined_at_snapshot")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.TargetRoleSnapshot)
            .HasColumnName("target_role_snapshot")
            .HasMaxLength(30)
            .HasColumnType("enum('ADMIN','MEMBER','OWNER')")
            .IsRequired(true);

        builder.Property(entity => entity.BannedByMemberId)
            .HasColumnName("banned_by_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.BannedByRole)
            .HasColumnName("banned_by_role")
            .HasMaxLength(30)
            .HasColumnType("enum('ADMIN','MEMBER','OWNER')")
            .IsRequired(true);

        builder.Property(entity => entity.BannedAt)
            .HasColumnName("banned_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.Reason)
            .HasColumnName("reason")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(true);

        builder.Property(entity => entity.ReleasedByMemberId)
            .HasColumnName("released_by_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.ReleasedAt)
            .HasColumnName("released_at")
            .HasColumnType("datetime(6)");

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => new { entity.ChatRoomId, entity.TargetUserId, entity.ReleasedAt }, "idx_open_chat_ban_room_user_active")
            .HasDatabaseName("idx_open_chat_ban_room_user_active");

        builder.HasIndex(entity => new { entity.ChatRoomId, entity.ReleasedAt, entity.Id }, "idx_open_chat_ban_room_active_id")
            .HasDatabaseName("idx_open_chat_ban_room_active_id");

        builder.HasIndex(entity => entity.TargetMemberCode, "idx_open_chat_ban_member_code")
            .HasDatabaseName("idx_open_chat_ban_member_code");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ChatRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ChatRoomMemberEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.TargetChatRoomMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ChatRoomMemberEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.BannedByMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ChatRoomMemberEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ReleasedByMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
