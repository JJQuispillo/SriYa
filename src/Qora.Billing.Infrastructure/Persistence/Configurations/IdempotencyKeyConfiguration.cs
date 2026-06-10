using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(k => k.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(k => k.Key)
            .HasColumnName("idempotency_key")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(k => k.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(k => k.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(k => k.ResponseSnapshot)
            .HasColumnName("response_snapshot")
            .HasColumnType("jsonb");

        builder.Property(k => k.DocumentId)
            .HasColumnName("document_id");

        builder.Property(k => k.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(k => k.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(k => k.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Unicidad por tenant + clave: actúa como lock de la inserción inicial (insert-as-lock).
        builder.HasIndex(k => new { k.TenantId, k.Key })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_keys_tenant_key");

        // Índice de expiración para la barrida de retención.
        builder.HasIndex(k => k.ExpiresAt)
            .HasDatabaseName("ix_idempotency_keys_expires_at");

        builder.Ignore(k => k.DomainEvents);
    }
}
