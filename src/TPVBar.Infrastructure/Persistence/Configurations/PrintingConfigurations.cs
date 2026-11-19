using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Printing;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class PrinterConfiguration : IEntityTypeConfiguration<Printer>
{
    public void Configure(EntityTypeBuilder<Printer> builder)
    {
        builder.ToTable("printers", t => t.HasCheckConstraint("ck_printers_paper_width", "paper_width_mm IN (58, 80)"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(80).IsRequired();
        builder.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Connection).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Target).HasMaxLength(200).IsRequired();
        builder.Property(p => p.LastError).HasMaxLength(500);
        builder.Ignore(p => p.IsSimulated);
        builder.HasIndex(p => p.Name).IsUnique().HasDatabaseName("ux_printers_name");
    }
}

internal sealed class PrinterRouteConfiguration : IEntityTypeConfiguration<PrinterRoute>
{
    public void Configure(EntityTypeBuilder<PrinterRoute> builder)
    {
        builder.ToTable("printer_routes");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Station).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<Printer>().WithMany().HasForeignKey(r => r.PrinterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.Station).IsUnique().HasDatabaseName("ux_printer_routes_station");
    }
}

internal sealed class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.ToTable("print_jobs", t =>
        {
            t.HasCheckConstraint("ck_print_jobs_attempts", "attempts >= 0");
            t.HasCheckConstraint("ck_print_jobs_max_attempts", "max_attempts > 0");
        });

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).UseIdentityAlwaysColumn();
        builder.Property(j => j.JobType).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.ReferenceType).HasMaxLength(30).IsRequired();
        builder.Property(j => j.ReferenceId).HasMaxLength(80).IsRequired();
        builder.Property(j => j.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.DedupeKey).HasMaxLength(200).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.DeliveryLevel).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.LockedBy).HasMaxLength(80);
        builder.Property(j => j.LastError).HasMaxLength(1000);

        builder.Property(j => j.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne<Printer>().WithMany().HasForeignKey(j => j.PrinterId).OnDelete(DeleteBehavior.Restrict);

        // Un mismo trabajo lógico (p. ej. el ticket de cocina de un envío) no se puede encolar dos veces.
        builder.HasIndex(j => j.DedupeKey).IsUnique().HasDatabaseName("ux_print_jobs_dedupe");
        builder.HasIndex(j => new { j.Status, j.NextAttemptAt }).HasDatabaseName("ix_print_jobs_pending");
    }
}

internal sealed class PrintJobAttemptConfiguration : IEntityTypeConfiguration<PrintJobAttempt>
{
    public void Configure(EntityTypeBuilder<PrintJobAttempt> builder)
    {
        builder.ToTable("print_job_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.Agent).HasMaxLength(80).IsRequired();
        builder.Property(a => a.DeliveryLevel).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Error).HasMaxLength(1000);
        builder.HasOne<PrintJob>().WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.JobId).HasDatabaseName("ix_print_job_attempts_job");
    }
}

internal sealed class PrintAgentHeartbeatConfiguration : IEntityTypeConfiguration<PrintAgentHeartbeat>
{
    public void Configure(EntityTypeBuilder<PrintAgentHeartbeat> builder)
    {
        builder.ToTable("print_agent_heartbeats");
        builder.HasKey(b => b.AgentName);
        builder.Property(b => b.AgentName).HasMaxLength(80);
        builder.Property(b => b.Version).HasMaxLength(40);
    }
}
