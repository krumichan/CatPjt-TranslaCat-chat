using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal sealed class OpenChatMemberProfileConfiguration : IEntityTypeConfiguration<OpenChatMemberProfileEntity>
{
    public void Configure(EntityTypeBuilder<OpenChatMemberProfileEntity> builder)
    {
        builder.ToTable("open_chat_member_profile");
        builder.HasKey(entity => entity.Id);

        // 기존 저장 형식과 nullable/길이를 명시해 provider 기본값에 맡기지 않는다.
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChatRoomMemberId)
            .HasColumnName("chat_room_member_id")
            .HasColumnType("bigint");

        builder.Property(entity => entity.MemberCode)
            .HasColumnName("member_code")
            .HasMaxLength(20)
            .HasColumnType("varchar(20)")
            .IsRequired(true);

        builder.Property(entity => entity.Nickname)
            .HasColumnName("nickname")
            .HasMaxLength(50)
            .HasColumnType("varchar(50)")
            .IsRequired(true);

        builder.Property(entity => entity.ProfileImageObjectKey)
            .HasColumnName("profile_image_object_key")
            .HasMaxLength(500)
            .HasColumnType("varchar(500)")
            .IsRequired(false);

        ChatAuditConfiguration.Configure(builder, true);

        // 원본의 고유성과 조회 인덱스를 보존하며 필터를 임의로 추가하지 않는다.
        builder.HasIndex(entity => entity.ChatRoomMemberId, "uk_open_chat_member_profile_member")
            .HasDatabaseName("uk_open_chat_member_profile_member")
            .IsUnique();

        builder.HasIndex(entity => entity.MemberCode, "uk_open_chat_member_profile_code")
            .HasDatabaseName("uk_open_chat_member_profile_code")
            .IsUnique();

        builder.HasIndex(entity => entity.Nickname, "idx_open_chat_member_profile_nickname")
            .HasDatabaseName("idx_open_chat_member_profile_nickname");

        // CHAT 내부 관계만 FK로 연결한다. BE 사용자 식별자는 외부 참조 값이다.
        builder.HasOne<ChatRoomMemberEntity>()
            .WithOne()
            .HasForeignKey<OpenChatMemberProfileEntity>(entity => entity.ChatRoomMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
