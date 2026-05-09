using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Persistence.Converters;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class TenantConfiguration(string encryptionKey) : IEntityTypeConfiguration<Tenant>
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

        // ── Email delivery settings ──────────────────────────────────
        builder.Property(t => t.EmailEnabled)
            .HasColumnName("email_enabled")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(t => t.EmailProvider)
            .HasColumnName("email_provider")
            .HasDefaultValue(EmailProvider.Qora)
            .IsRequired();

        builder.Property(t => t.SmtpHost)
            .HasColumnName("smtp_host")
            .HasMaxLength(255);

        builder.Property(t => t.SmtpPort)
            .HasColumnName("smtp_port");

        builder.Property(t => t.SmtpUser)
            .HasColumnName("smtp_user")
            .HasMaxLength(255);

        builder.Property(t => t.SmtpPassword)
            .HasColumnName("smtp_password")
            .HasMaxLength(512)
            .HasConversion(new EncryptedStringConverter(encryptionKey));

        builder.Property(t => t.UseSsl)
            .HasColumnName("use_ssl")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.SenderEmail)
            .HasColumnName("sender_email")
            .HasMaxLength(255);

        builder.Property(t => t.SenderName)
            .HasColumnName("sender_name")
            .HasMaxLength(255);

        // Ignore domain events collection
        builder.Ignore(t => t.DomainEvents);
    }
}
