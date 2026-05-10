using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    // Fixed GUIDs so seed data is deterministic across migrations
    public static readonly Guid FreePlanId  = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid BasicPlanId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid ProPlanId   = new("00000000-0000-0000-0000-000000000003");

    private static readonly DateTime SeedDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(p => p.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(p => p.Slug)
            .HasColumnName("slug")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.DocumentLimit)
            .HasColumnName("document_limit")
            .IsRequired();

        builder.Property(p => p.PriceMonthlyUsd)
            .HasColumnName("price_monthly_usd")
            .HasColumnType("numeric(10,2)")
            .IsRequired();

        builder.Property(p => p.StripeProductId)
            .HasColumnName("stripe_product_id")
            .HasMaxLength(255);

        builder.Property(p => p.StripePriceId)
            .HasColumnName("stripe_price_id")
            .HasMaxLength(255);

        builder.Property(p => p.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Unique index on slug for lookups
        builder.HasIndex(p => p.Slug)
            .IsUnique()
            .HasDatabaseName("ix_plans_slug");

        // Ignore domain events collection
        builder.Ignore(p => p.DomainEvents);

        // Seed data
        builder.HasData(
            new
            {
                Id = FreePlanId,
                Name = "Free",
                Slug = "free",
                DocumentLimit = 50,
                PriceMonthlyUsd = 0.00m,
                StripeProductId = (string?)null,
                StripePriceId = (string?)null,
                IsActive = true,
                CreatedAt = SeedDate,
                UpdatedAt = SeedDate
            },
            new
            {
                Id = BasicPlanId,
                Name = "Basic",
                Slug = "basic",
                DocumentLimit = 500,
                PriceMonthlyUsd = 29.00m,
                StripeProductId = (string?)null,
                StripePriceId = (string?)null,
                IsActive = true,
                CreatedAt = SeedDate,
                UpdatedAt = SeedDate
            },
            new
            {
                Id = ProPlanId,
                Name = "Pro",
                Slug = "pro",
                DocumentLimit = -1,
                PriceMonthlyUsd = 99.00m,
                StripeProductId = (string?)null,
                StripePriceId = (string?)null,
                IsActive = true,
                CreatedAt = SeedDate,
                UpdatedAt = SeedDate
            });
    }
}
