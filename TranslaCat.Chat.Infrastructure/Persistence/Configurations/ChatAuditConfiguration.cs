using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TranslaCat.Chat.Infrastructure.Persistence.Configurations;

internal static class ChatAuditConfiguration
{
    public static void Configure<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        bool includesUpdateAudit)
        where TEntity : class
    {
        // 감사값은 저장 호출자가 제공한다. 시각대 변환이나 현재 시각 추론은 하지 않는다.
        builder.Property<string?>("CreatedBy")
            .HasColumnName("created_by")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsRequired(false)
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.Property<DateTime>("CreatedAt")
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired()
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        if (!includesUpdateAudit)
        {
            return;
        }

        builder.Property<string?>("UpdatedBy")
            .HasColumnName("updated_by")
            .HasColumnType("varchar(50)")
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property<DateTime>("UpdatedAt")
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();
    }
}
