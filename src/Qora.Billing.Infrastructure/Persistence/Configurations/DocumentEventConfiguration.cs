using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class DocumentEventConfiguration : IEntityTypeConfiguration<DocumentEvent>
{
    public void Configure(EntityTypeBuilder<DocumentEvent> builder)
    {
        builder.ToTable("document_events");

        builder.HasKey(de => de.Id);
        builder.Property(de => de.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(de => de.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(de => de.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(de => de.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(de => de.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(de => de.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        // Relaciones
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(de => de.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índices
        builder.HasIndex(de => de.DocumentId)
            .HasDatabaseName("ix_document_events_document_id");

        builder.HasIndex(de => de.TenantId)
            .HasDatabaseName("ix_document_events_tenant_id");
    }
}
