using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(t => t.Ruc)
            .HasColumnName("ruc")
            .HasMaxLength(13)
            .IsRequired()
            .HasConversion(
                ruc => ruc.Value,
                value => new Ruc(value));

        builder.Property(t => t.BusinessName)
            .HasColumnName("business_name")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(t => t.TradeName)
            .HasColumnName("trade_name")
            .HasMaxLength(300);

        builder.Property(t => t.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(t => t.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Relationships
        builder.HasMany<Document>()
            .WithOne()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany<ApiKey>()
            .WithOne()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany<ElectronicSignature>()
            .WithOne()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        builder.HasIndex(t => t.Ruc)
            .IsUnique()
            .HasDatabaseName("ix_tenants_ruc");

        // Ignore domain events collection
        builder.Ignore(t => t.DomainEvents);
    }
}
