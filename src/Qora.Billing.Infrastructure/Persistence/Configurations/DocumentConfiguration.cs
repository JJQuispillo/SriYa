using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(d => d.DocumentType)
            .HasColumnName("document_type")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(d => d.AccessKey)
            .HasColumnName("access_key")
            .HasMaxLength(49)
            .HasConversion(
                ak => ak != null ? ak.Value : null,
                value => value != null ? new AccessKey(value) : null);

        builder.Property(d => d.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(d => d.XmlContent)
            .HasColumnName("xml_content")
            .HasColumnType("text");

        builder.Property(d => d.SignedXmlContent)
            .HasColumnName("signed_xml_content")
            .HasColumnType("text");

        builder.Property(d => d.SriAuthorizationNumber)
            .HasColumnName("sri_authorization_number")
            .HasMaxLength(49);

        builder.Property(d => d.SriAuthorizationDate)
            .HasColumnName("sri_authorization_date");

        var dictionaryConverter = new ValueConverter<Dictionary<string, string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                ?? new Dictionary<string, string>());

        // Comparer requerido por EF para propiedades de tipo colección con converter:
        // permite el snapshot y la detección de cambios correcta (igualdad por contenido,
        // no por referencia, y copia profunda al tomar el snapshot).
        var dictionaryComparer = new ValueComparer<Dictionary<string, string>>(
            (a, b) => a == null
                ? b == null
                : b != null && a.Count == b.Count && !a.Except(b).Any(),
            v => v == null
                ? 0
                : v.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
            v => v == null ? new Dictionary<string, string>() : new Dictionary<string, string>(v));

        builder.Property(d => d.IssuerInfo)
            .HasColumnName("issuer_info")
            .HasColumnType("jsonb")
            .HasConversion(dictionaryConverter, dictionaryComparer)
            .IsRequired();

        builder.Property(d => d.BuyerInfo)
            .HasColumnName("buyer_info")
            .HasColumnType("jsonb")
            .HasConversion(dictionaryConverter, dictionaryComparer)
            .IsRequired();

        builder.Property(d => d.ErrorMessage)
            .HasColumnName("error_message")
            .HasColumnType("text");

        builder.Property(d => d.RetryCount)
            .HasColumnName("retry_count")
            .HasDefaultValue(0);

        builder.Property(d => d.NextRetryAt)
            .HasColumnName("next_retry_at");

        builder.Property(d => d.ProcessedAt)
            .HasColumnName("processed_at");

        builder.Property(d => d.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(d => d.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Relaciones
        builder.HasMany(d => d.Items)
            .WithOne()
            .HasForeignKey(i => i.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Navegación hacia DocumentEvents (sin propiedad de navegación en Document, configurada desde el lado de DocumentEvent)

        // Índices
        builder.HasIndex(d => new { d.TenantId, d.Status })
            .HasDatabaseName("ix_documents_tenant_id_status");

        builder.HasIndex(d => d.AccessKey)
            .IsUnique()
            .HasDatabaseName("ix_documents_access_key")
            .HasFilter("access_key IS NOT NULL");

        // Ignorar la colección de eventos de dominio (no se persiste)
        builder.Ignore(d => d.DomainEvents);
    }
}
