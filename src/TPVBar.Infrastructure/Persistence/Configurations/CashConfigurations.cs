using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Sales;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> builder)
    {
        builder.ToTable("payment_methods");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Code).HasMaxLength(30).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(60).IsRequired();
        builder.HasIndex(m => m.Code).IsUnique().HasDatabaseName("ux_payment_methods_code");
    }
}

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("cash_sessions", t =>
        {
            t.HasCheckConstraint("ck_cash_sessions_opening", "opening_amount >= 0");
            t.HasCheckConstraint("ck_cash_sessions_close", "(status = 'Open' AND closed_at IS NULL) OR (status = 'Closed' AND closed_at IS NOT NULL)");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).UseIdentityAlwaysColumn();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(s => s.OpeningAmount).HasPrecision(12, 2);
        builder.Property(s => s.ExpectedCash).HasPrecision(12, 2);
        builder.Property(s => s.CountedCash).HasPrecision(12, 2);
        builder.Property(s => s.Difference).HasPrecision(12, 2);
        builder.Property(s => s.Notes).HasMaxLength(500);

        builder.Property(s => s.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Una sola caja abierta a la vez: lo garantiza la base de datos aunque dos puestos abran simultáneamente.
        builder.HasIndex(s => s.Status).IsUnique().HasFilter("status = 'Open'").HasDatabaseName("ux_cash_sessions_one_open");
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable("cash_movements", t => t.HasCheckConstraint("ck_cash_movements_amount", "amount > 0"));
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Amount).HasPrecision(12, 2);
        builder.Property(m => m.Concept).HasMaxLength(200).IsRequired();
        builder.HasOne<CashSession>().WithMany().HasForeignKey(m => m.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => m.CashSessionId).HasDatabaseName("ix_cash_movements_session");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", t =>
        {
            t.HasCheckConstraint("ck_payments_amount", "amount > 0");
            t.HasCheckConstraint("ck_payments_change", "change_given >= 0 AND (tendered_amount IS NULL OR tendered_amount - amount = change_given)");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).UseIdentityAlwaysColumn();
        builder.Property(p => p.Amount).HasPrecision(12, 2);
        builder.Property(p => p.TenderedAmount).HasPrecision(12, 2);
        builder.Property(p => p.ChangeGiven).HasPrecision(12, 2);
        builder.Property(p => p.ExternalReference).HasMaxLength(120);
        builder.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(p => p.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(p => p.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.OrderId).HasDatabaseName("ix_payments_order");
        builder.HasIndex(p => new { p.CashSessionId, p.PaidAt }).HasDatabaseName("ix_payments_session_date");
    }
}

internal sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    public void Configure(EntityTypeBuilder<PaymentRefund> builder)
    {
        builder.ToTable("payment_refunds", t => t.HasCheckConstraint("ck_payment_refunds_amount", "amount > 0"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityAlwaysColumn();
        builder.Property(r => r.Amount).HasPrecision(12, 2);
        builder.Property(r => r.Reason).HasMaxLength(200).IsRequired();
        builder.HasOne<Payment>().WithMany().HasForeignKey(r => r.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(r => r.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(r => r.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.PaymentId).HasDatabaseName("ix_payment_refunds_payment");
        builder.HasIndex(r => r.CashSessionId).HasDatabaseName("ix_payment_refunds_session");
    }
}
