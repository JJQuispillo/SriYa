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

        // CertificateData: se almacena cifrado como Base64 en una columna text.
        // El tipo de dominio es byte[] no anulable, por lo que los delegados lambda hacen de puente
        // hacia el conversor de tipo anulable mediante los helpers estáticos internos.
        builder.Property(e => e.CertificateData)
            .HasColumnName("certificate_data")
            .HasColumnType("text")
            .IsRequired()
            .HasConversion(
                v => EncryptedBytesConverter.EncryptValue(v, encryptionKey),
                v => EncryptedBytesConverter.DecryptValue(v, encryptionKey));

        // PasswordEncrypted: columna text cifrada.
        // El tipo de dominio es string no anulable; se hace de puente mediante los helpers estáticos internos.
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

        // Índices
        builder.HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_electronic_signatures_tenant_id");

        // Ignorar la colección de eventos de dominio
        builder.Ignore(e => e.DomainEvents);
    }
}
