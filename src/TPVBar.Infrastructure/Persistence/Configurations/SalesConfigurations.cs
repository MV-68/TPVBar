using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Floor;
using TPVBar.Domain.Sales;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_guest_count", "guest_count > 0");
            t.HasCheckConstraint("ck_orders_totals", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND grand_total >= 0");
            t.HasCheckConstraint("ck_orders_total_matches", "grand_total = subtotal + tax_total");
            t.HasCheckConstraint("ck_orders_paid_total", "paid_total >= 0 AND paid_total <= grand_total");
            t.HasCheckConstraint("ck_orders_closed", "(status = 'Open' AND closed_at IS NULL) OR status <> 'Open'");
        });

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).UseIdentityAlwaysColumn();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.Notes).HasMaxLength(500);
        builder.Property(o => o.Subtotal).HasPrecision(12, 2);
        builder.Property(o => o.DiscountTotal).HasPrecision(12, 2);
        builder.Property(o => o.TaxTotal).HasPrecision(12, 2);
        builder.Property(o => o.GrandTotal).HasPrecision(12, 2);
        builder.Property(o => o.PaidTotal).HasPrecision(12, 2);
        builder.Ignore(o => o.Balance);

        builder.Property(o => o.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne<DiningTable>().WithMany().HasForeignKey(o => o.TableId).OnDelete(DeleteBehavior.Restrict);

        // Una sola comanda viva por mesa: lo garantiza la base de datos aunque dos terminales actúen a la vez.
        builder.HasIndex(o => o.TableId)
            .IsUnique()
            .HasFilter("status IN ('Open','PendingPayment')")
            .HasDatabaseName("ux_orders_one_live_per_table");
        builder.HasIndex(o => o.OpenedAt).HasDatabaseName("ix_orders_opened_at");
        builder.HasIndex(o => o.Status).HasDatabaseName("ix_orders_status");

        builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", t =>
        {
            t.HasCheckConstraint("ck_order_lines_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_order_lines_price", "unit_price >= 0");
            t.HasCheckConstraint("ck_order_lines_total", "line_total >= 0");
        });

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).UseIdentityAlwaysColumn();
        builder.Property(l => l.ProductNameSnapshot).HasMaxLength(120).IsRequired();
        builder.Property(l => l.Quantity).HasPrecision(12, 3);
        builder.Property(l => l.UnitPrice).HasPrecision(12, 2);
        builder.Property(l => l.TaxRateSnapshot).HasPrecision(6, 3);
        builder.Property(l => l.DiscountAmount).HasPrecision(12, 2);
        builder.Property(l => l.LineTotal).HasPrecision(12, 2);
        builder.Property(l => l.Notes).HasMaxLength(200);
        builder.Property(l => l.VoidReason).HasMaxLength(200);
        builder.Property(l => l.Station).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Ignore(l => l.IsActive);

        builder.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => l.OrderId).HasDatabaseName("ix_order_lines_order");
    }
}

internal sealed class KitchenTicketConfiguration : IEntityTypeConfiguration<KitchenTicket>
{
    public void Configure(EntityTypeBuilder<KitchenTicket> builder)
    {
        builder.ToTable("kitchen_tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).UseIdentityAlwaysColumn();
        builder.Property(t => t.Station).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.TableLabel).HasMaxLength(130).IsRequired();

        builder.HasOne<Order>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.Status, t.SentAt }).HasDatabaseName("ix_kitchen_tickets_status");

        builder.HasMany(t => t.Lines).WithOne().HasForeignKey(l => l.KitchenTicketId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(t => t.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class KitchenTicketLineConfiguration : IEntityTypeConfiguration<KitchenTicketLine>
{
    public void Configure(EntityTypeBuilder<KitchenTicketLine> builder)
    {
        builder.ToTable("kitchen_ticket_lines", t => t.HasCheckConstraint("ck_kitchen_ticket_lines_quantity", "quantity > 0"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).UseIdentityAlwaysColumn();
        builder.Property(l => l.Quantity).HasPrecision(12, 3);
        builder.Property(l => l.ProductNameSnapshot).HasMaxLength(120).IsRequired();
        builder.Property(l => l.Notes).HasMaxLength(200);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);

        // La línea del ticket apunta a la línea de la comanda; sin navegación inversa.
        builder.HasOne(l => l.OrderLine).WithMany().HasForeignKey(l => l.OrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}
