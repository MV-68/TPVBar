using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Floor;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> builder)
    {
        builder.ToTable("zones");
        builder.HasKey(z => z.Id);
        builder.Property(z => z.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(z => z.Name).IsUnique().HasDatabaseName("ux_zones_name");
    }
}

internal sealed class DiningTableConfiguration : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> builder)
    {
        builder.ToTable("dining_tables", t => t.HasCheckConstraint("ck_dining_tables_capacity", "capacity > 0"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(40).IsRequired();
        builder.HasOne<Zone>().WithMany().HasForeignKey(t => t.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.ZoneId, t.Name }).IsUnique().HasDatabaseName("ux_dining_tables_zone_name");
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(c => c.Name).IsUnique().HasDatabaseName("ux_categories_name");
    }
}

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable("tax_rates", t => t.HasCheckConstraint("ck_tax_rates_rate", "rate >= 0 AND rate <= 100"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(60).IsRequired();
        builder.Property(t => t.Rate).HasPrecision(6, 3);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
        {
            t.HasCheckConstraint("ck_products_price", "sale_price >= 0");
            t.HasCheckConstraint("ck_products_stock_minimum", "stock_minimum >= 0");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.StockQuantity).HasPrecision(12, 3);
        builder.Property(p => p.StockMinimum).HasPrecision(12, 3);
        builder.Property(p => p.StockUnit).HasMaxLength(20).IsRequired();
        builder.Ignore(p => p.IsLowStock);

        // Concurrencia optimista: dos envíos simultáneos del mismo producto nunca pisan el stock del otro.
        builder.Property(p => p.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
        builder.Property(p => p.Name).HasMaxLength(120).IsRequired();
        builder.Property(p => p.Sku).HasMaxLength(40);
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.SalePrice).HasPrecision(12, 2);
        builder.Property(p => p.Station).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TaxRate>().WithMany().HasForeignKey(p => p.TaxRateId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Sku).IsUnique().HasFilter("sku IS NOT NULL").HasDatabaseName("ux_products_sku");
        builder.HasIndex(p => new { p.CategoryId, p.IsActive, p.DisplayOrder }).HasDatabaseName("ix_products_category_active");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Key).HasMaxLength(120).IsRequired();
        builder.Property(r => r.Operation).HasMaxLength(80).IsRequired();
        builder.HasIndex(r => r.Key).IsUnique().HasDatabaseName("ux_idempotency_key");
    }
}
