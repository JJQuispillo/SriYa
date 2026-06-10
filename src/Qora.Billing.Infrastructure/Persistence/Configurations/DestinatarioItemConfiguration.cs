using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class DestinatarioItemConfiguration : IEntityTypeConfiguration<DestinatarioItem>
{
    public void Configure(EntityTypeBuilder<DestinatarioItem> builder)
    {
        builder.ToTable("document_destinatario_items");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(i => i.DestinatarioId)
            .HasColumnName("destinatario_id")
            .IsRequired();

        builder.Property(i => i.CodigoInterno)
            .HasColumnName("codigo_interno")
            .HasMaxLength(25)
            .IsRequired();

        builder.Property(i => i.DescripcionDetalle)
            .HasColumnName("descripcion_detalle")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(i => i.CantidadDetalle)
            .HasColumnName("cantidad_detalle")
            .HasColumnType("decimal(18,6)")
            .IsRequired();

        builder.Property(i => i.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(i => i.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Índice para búsquedas por destinatario
        builder.HasIndex(i => i.DestinatarioId)
            .HasDatabaseName("ix_document_destinatario_items_destinatario_id");

        // Ignorar la colección de eventos de dominio (no se persiste)
        builder.Ignore(i => i.DomainEvents);
    }
}
