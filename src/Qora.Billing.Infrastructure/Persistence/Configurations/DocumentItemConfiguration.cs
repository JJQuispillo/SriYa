using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class DocumentItemConfiguration : IEntityTypeConfiguration<DocumentItem>
{
    public void Configure(EntityTypeBuilder<DocumentItem> builder)
    {
        builder.ToTable("document_items");

        builder.HasKey(di => di.Id);
        builder.Property(di => di.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(di => di.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(di => di.MainCode)
            .HasColumnName("main_code")
            .HasMaxLength(25)
            .IsRequired();

        builder.Property(di => di.AuxiliaryCode)
            .HasColumnName("auxiliary_code")
            .HasMaxLength(25);

        builder.Property(di => di.Description)
            .HasColumnName("description")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(di => di.Quantity)
            .HasColumnName("quantity")
            .HasColumnType("decimal(18,6)")
            .IsRequired();

        builder.Property(di => di.UnitPrice)
            .HasColumnName("unit_price")
            .HasColumnType("decimal(18,6)")
            .IsRequired();

        builder.Property(di => di.Discount)
            .HasColumnName("discount")
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(di => di.TaxRate)
            .HasColumnName("tax_rate")
            .HasColumnType("decimal(5,2)")
            .IsRequired();

        builder.Property(di => di.TaxCode)
            .HasColumnName("tax_code")
            .HasMaxLength(5)
            .IsRequired();

        builder.Property(di => di.TaxPercentageCode)
            .HasColumnName("tax_percentage_code")
            .HasMaxLength(5)
            .IsRequired();

        builder.Property(x => x.SustentoDocumentType)
            .HasColumnName("sustento_document_type")
            .HasMaxLength(2)
            .IsRequired(false);

        builder.Property(x => x.SustentoDocumentNumber)
            .HasColumnName("sustento_document_number")
            .HasMaxLength(39)
            .IsRequired(false);

        builder.Property(x => x.SustentoDocumentIssueDate)
            .HasColumnName("sustento_document_issue_date")
            .IsRequired(false);

        builder.Property(x => x.SustentoDocumentAuthNumber)
            .HasColumnName("sustento_document_auth_number")
            .HasMaxLength(49)
            .IsRequired(false);

        // Subtotal is computed, ignore from persistence
        builder.Ignore(di => di.Subtotal);

        // Index for lookups by document
        builder.HasIndex(di => di.DocumentId)
            .HasDatabaseName("ix_document_items_document_id");
    }
}
