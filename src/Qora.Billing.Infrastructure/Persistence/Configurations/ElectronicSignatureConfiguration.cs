using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Infrastructure.Persistence.Converters;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class ElectronicSignatureConfiguration(string encryptionKey) : IEntityTypeConfiguration<ElectronicSignature>
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

        // CertificateData: stored encrypted as Base64 in a text column.
        // Domain type is non-nullable byte[], so lambda delegates bridge to
        // the nullable-typed converter via the internal static helpers.
        builder.Property(e => e.CertificateData)
            .HasColumnName("certificate_data")
            .HasColumnType("text")
            .IsRequired()
            .HasConversion(
                v => EncryptedBytesConverter.EncryptValue(v, encryptionKey),
                v => EncryptedBytesConverter.DecryptValue(v, encryptionKey));

        // PasswordEncrypted: encrypted text column.
        // Domain type is non-nullable string; bridged via internal static helpers.
        builder.Property(e => e.PasswordEncrypted)
            .HasColumnName("password_encrypted")
            .HasColumnType("varchar(500)")
            .IsRequired()
            .HasConversion(
                v => EncryptedStringConverter.EncryptValue(v, encryptionKey),
                v => EncryptedStringConverter.DecryptValue(v, encryptionKey));

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
