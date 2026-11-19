using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Sales;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices", t =>
        {
            t.HasCheckConstraint("ck_invoices_totals", "base + tax = total");
            t.HasCheckConstraint("ck_invoices_sign", "(kind = 'Rectificative' AND total < 0) OR (kind <> 'Rectificative' AND total > 0)");
            t.HasCheckConstraint("ck_invoices_recipient", "kind <> 'Complete' OR (recipient_name IS NOT NULL AND recipient_nif IS NOT NULL AND recipient_address IS NOT NULL)");
            t.HasCheckConstraint("ck_invoices_rectified", "(kind = 'Rectificative') = (rectified_invoice_id IS NOT NULL)");
            t.HasCheckConstraint("ck_invoices_number", "number > 0");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).UseIdentityAlwaysColumn();
        builder.Property(i => i.Series).HasMaxLength(12).IsRequired();
        builder.Property(i => i.FullNumber).HasMaxLength(30).IsRequired();
        builder.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.TypeCode).HasMaxLength(2).IsRequired();
        builder.Property(i => i.IssueDate).HasColumnType("date");
        builder.Property(i => i.IssuerName).HasMaxLength(120).IsRequired();
        builder.Property(i => i.IssuerNif).HasMaxLength(9).IsRequired();
        builder.Property(i => i.IssuerAddress).HasMaxLength(250).IsRequired();
        builder.Property(i => i.RecipientName).HasMaxLength(120);
        builder.Property(i => i.RecipientNif).HasMaxLength(9);
        builder.Property(i => i.RecipientAddress).HasMaxLength(250);
        builder.Property(i => i.RectificationReason).HasMaxLength(250);
        builder.Property(i => i.Base).HasPrecision(12, 2);
        builder.Property(i => i.Tax).HasPrecision(12, 2);
        builder.Property(i => i.Total).HasPrecision(12, 2);

        builder.HasOne<Order>().WithMany().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(i => i.RectifiedInvoiceId).OnDelete(DeleteBehavior.Restrict);

        // Numeración correlativa sin duplicados: lo garantiza la base de datos aunque dos puestos emitan a la vez.
        builder.HasIndex(i => new { i.Series, i.Number }).IsUnique().HasDatabaseName("ux_invoices_series_number");
        builder.HasIndex(i => i.FullNumber).IsUnique().HasDatabaseName("ux_invoices_full_number");
        builder.HasIndex(i => i.OrderId).HasDatabaseName("ix_invoices_order");
        builder.HasIndex(i => i.RectifiedInvoiceId).HasDatabaseName("ix_invoices_rectified");
        builder.HasIndex(i => i.IssueDate).HasDatabaseName("ix_invoices_issue_date");

        builder.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines", t => t.HasCheckConstraint("ck_invoice_lines_totals", "base + tax = total"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).UseIdentityAlwaysColumn();
        builder.Property(l => l.Description).HasMaxLength(200).IsRequired();
        builder.Property(l => l.Quantity).HasPrecision(12, 3);
        builder.Property(l => l.UnitPrice).HasPrecision(12, 2);
        builder.Property(l => l.TaxRate).HasPrecision(6, 3);
        builder.Property(l => l.Base).HasPrecision(12, 2);
        builder.Property(l => l.Tax).HasPrecision(12, 2);
        builder.Property(l => l.Total).HasPrecision(12, 2);
        builder.HasIndex(l => l.InvoiceId).HasDatabaseName("ix_invoice_lines_invoice");
        builder.HasIndex(l => l.RectifiesLineId).HasDatabaseName("ix_invoice_lines_rectifies");
    }
}

internal sealed class BillingRecordConfiguration : IEntityTypeConfiguration<BillingRecord>
{
    public void Configure(EntityTypeBuilder<BillingRecord> builder)
    {
        builder.ToTable("billing_records", t =>
        {
            t.HasCheckConstraint("ck_billing_records_sequence", "sequence > 0");
            t.HasCheckConstraint("ck_billing_records_hash", "char_length(hash) = 64");
            t.HasCheckConstraint("ck_billing_records_corrects", "corrects_sequence IS NULL OR corrects_sequence < sequence");
        });

        builder.HasKey(r => r.Sequence);
        builder.Property(r => r.Sequence).ValueGeneratedNever();
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(r => r.IssuerNif).HasMaxLength(9).IsRequired();
        builder.Property(r => r.InvoiceNumber).HasMaxLength(30).IsRequired();
        builder.Property(r => r.IssueDate).HasMaxLength(10).IsRequired();
        builder.Property(r => r.TypeCode).HasMaxLength(2).IsRequired();
        builder.Property(r => r.TaxTotal).HasPrecision(12, 2);
        builder.Property(r => r.GrandTotal).HasPrecision(12, 2);
        builder.Property(r => r.PreviousHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Hash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.GeneratedAtText).HasMaxLength(40).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(250);
        builder.HasOne(r => r.Invoice).WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        // Un alta y, como mucho, una anulación por factura; y cada huella anterior se usa una sola vez (la cadena no se bifurca).
        builder.HasIndex(r => new { r.InvoiceId, r.Type }).IsUnique().HasFilter("corrects_sequence IS NULL").HasDatabaseName("ux_billing_records_invoice_type");
        builder.HasIndex(r => r.Hash).IsUnique().HasDatabaseName("ux_billing_records_hash");
        builder.HasIndex(r => r.PreviousHash).IsUnique().HasDatabaseName("ux_billing_records_previous_hash");
    }
}

internal sealed class BillingChainHeadConfiguration : IEntityTypeConfiguration<BillingChainHead>
{
    public void Configure(EntityTypeBuilder<BillingChainHead> builder)
    {
        builder.ToTable("billing_chain_head", t => t.HasCheckConstraint("ck_billing_chain_head_single", "id = 1"));
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.LastHash).HasMaxLength(64).IsRequired();
        builder.Property(h => h.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

internal sealed class BillingSubmissionConfiguration : IEntityTypeConfiguration<BillingSubmission>
{
    public void Configure(EntityTypeBuilder<BillingSubmission> builder)
    {
        builder.ToTable("billing_submissions");
        builder.HasKey(s => s.RecordSequence);
        builder.Property(s => s.RecordSequence).ValueGeneratedNever();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(s => s.Message).HasMaxLength(500);
        builder.Property(s => s.Csv).HasMaxLength(64);
        builder.HasOne<BillingRecord>().WithOne().HasForeignKey<BillingSubmission>(s => s.RecordSequence).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.Status).HasDatabaseName("ix_billing_submissions_status");
    }
}
