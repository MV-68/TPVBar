using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPVBar.Domain.Entities;

namespace TPVBar.Infrastructure.Persistence.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_failed_access_count", "failed_access_count >= 0");
        });

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.UserName).HasMaxLength(50).IsRequired();
        builder.Property(u => u.NormalizedUserName).HasMaxLength(50).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();

        // Concurrencia optimista con la columna de sistema xmin de PostgreSQL.
        builder.Property(u => u.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(u => u.NormalizedUserName).IsUnique().HasDatabaseName("ux_users_normalized_user_name");

        builder.HasMany(u => u.UserRoles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(u => u.UserRoles).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
