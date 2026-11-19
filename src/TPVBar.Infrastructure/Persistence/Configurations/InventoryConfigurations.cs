using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Inventory;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements", t => t.HasCheckConstraint("ck_stock_movements_delta", "quantity_delta <> 0"));
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();
        builder.Property(m => m.QuantityDelta).HasPrecision(12, 3);
        builder.Property(m => m.QuantityAfter).HasPrecision(12, 3);
        builder.Property(m => m.Reason).HasConversion<string>().HasMaxLength(30);
        builder.Property(m => m.ReferenceType).HasMaxLength(30);
        builder.Property(m => m.ReferenceId).HasMaxLength(80);
        builder.Property(m => m.Notes).HasMaxLength(500);
        builder.HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.ProductId, m.CreatedAt }).HasDatabaseName("ix_stock_movements_product_date");

        // Un movimiento ligado a una línea es único por motivo y línea: un reintento no puede descontar dos veces.
        builder.HasIndex(m => new { m.Reason, m.ReferenceType, m.ReferenceId })
            .IsUnique()
            .HasFilter("reference_id IS NOT NULL")
            .HasDatabaseName("ux_stock_movements_reference");
    }
}

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("app_settings");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(80);
        builder.Property(s => s.Value).HasMaxLength(500).IsRequired();
    }
}
