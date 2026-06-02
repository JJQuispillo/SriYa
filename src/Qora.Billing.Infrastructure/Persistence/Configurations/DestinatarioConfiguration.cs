using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class DestinatarioConfiguration : IEntityTypeConfiguration<Destinatario>
{
    public void Configure(EntityTypeBuilder<Destinatario> builder)
    {
        builder.ToTable("document_destinatarios");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(d => d.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(d => d.IdentificacionDestinatario)
            .HasColumnName("identificacion_destinatario")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(d => d.RazonSocialDestinatario)
            .HasColumnName("razon_social_destinatario")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(d => d.DirDestinatario)
            .HasColumnName("dir_destinatario")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(d => d.MotivoTraslado)
            .HasColumnName("motivo_traslado")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(d => d.RutaEntrega)
            .HasColumnName("ruta_entrega")
            .HasMaxLength(300);

        builder.Property(d => d.DocAduaneroUnico)
            .HasColumnName("doc_aduanero_unico")
            .HasMaxLength(20);

        builder.Property(d => d.CodEstabDestino)
            .HasColumnName("cod_estab_destino")
            .HasMaxLength(3);

        builder.Property(d => d.RucTransportista)
            .HasColumnName("ruc_transportista")
            .HasMaxLength(13)
            .IsRequired();

        builder.Property(d => d.Rise)
            .HasColumnName("rise")
            .HasMaxLength(40);

        builder.Property(d => d.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(d => d.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Relación: propiedad de Document, borrado en cascada
        builder.HasOne<Document>()
            .WithMany(doc => doc.Destinatarios)
            .HasForeignKey(d => d.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relación: posee DestinatarioItems
        builder.HasMany(d => d.Items)
            .WithOne()
            .HasForeignKey(i => i.DestinatarioId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índice para búsquedas por documento
        builder.HasIndex(d => d.DocumentId)
            .HasDatabaseName("ix_document_destinatarios_document_id");

        // Ignorar la colección de eventos de dominio (no se persiste)
        builder.Ignore(d => d.DomainEvents);
    }
}
