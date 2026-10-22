using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenInvitations;

// CHAT에 공통 사용자 row가 없으므로 초대의 발신자별 동시성만 이 row로 직렬화한다.
public sealed class OpenInvitationSenderGateEntity
{
    public long ActorUserId { get; set; }
    public DateTime TouchedAtUtc { get; set; }
}

internal sealed class OpenInvitationSenderGateConfiguration : IEntityTypeConfiguration<OpenInvitationSenderGateEntity>
{
    public void Configure(EntityTypeBuilder<OpenInvitationSenderGateEntity> builder)
    {
        builder.ToTable("chat_open_invitation_sender_gate");
        builder.HasKey(row => row.ActorUserId);
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("bigint").ValueGeneratedNever();
        builder.Property(row => row.TouchedAtUtc).HasColumnName("touched_at_utc").HasColumnType("datetime(6)");
    }
}
