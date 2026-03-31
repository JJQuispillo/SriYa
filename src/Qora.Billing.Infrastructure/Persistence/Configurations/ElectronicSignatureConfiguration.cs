using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class ElectronicSignatureConfiguration : IEntityTypeConfiguration<ElectronicSignature>
{
    public void Configure(EntityTypeBuilder<ElectronicSignature> builder)
    {
        builder.ToTable("electronic_signatures");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(e => e.CertificateData)
            .HasColumnName("certificate_data")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(e => e.PasswordEncrypted)
            .HasColumnName("password_encrypted")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(e => e.OwnerName)
            .HasColumnName("owner_name")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(e => e.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(e => e.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Indexes
        builder.HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_electronic_signatures_tenant_id");

        // Ignore domain events collection
        builder.Ignore(e => e.DomainEvents);
    }
}
