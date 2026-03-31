using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");

        builder.HasKey(ur => ur.Id);
        builder.Property(ur => ur.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(ur => ur.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(ur => ur.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(ur => ur.DocumentType)
            .HasColumnName("document_type")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(ur => ur.BillingPeriod)
            .HasColumnName("billing_period")
            .HasMaxLength(7) // "YYYY-MM"
            .IsRequired();

        builder.Property(ur => ur.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Relationships
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(ur => ur.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(ur => new { ur.TenantId, ur.BillingPeriod })
            .HasDatabaseName("ix_usage_records_tenant_id_billing_period");

        builder.HasIndex(ur => ur.DocumentId)
            .HasDatabaseName("ix_usage_records_document_id");
    }
}
