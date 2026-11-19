using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Auditing;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.Action).HasMaxLength(80).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(80).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Workstation).HasMaxLength(100);
        builder.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb").IsRequired();

        builder.HasIndex(e => new { e.EntityType, e.EntityId }).HasDatabaseName("ix_audit_entity");
        builder.HasIndex(e => e.OccurredAt).HasDatabaseName("ix_audit_date");
    }
}
