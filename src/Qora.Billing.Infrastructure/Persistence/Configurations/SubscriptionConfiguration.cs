using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Persistence.Converters;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class SubscriptionConfiguration(string encryptionKey) : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(s => s.PlanId)
            .HasColumnName("plan_id")
            .IsRequired();

        builder.Property(s => s.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // StripeCustomerId is PII — encrypt at rest
        builder.Property(s => s.StripeCustomerId)
            .HasColumnName("stripe_customer_id")
            .HasMaxLength(512)
            .HasConversion(new EncryptedStringConverter(encryptionKey));

        builder.Property(s => s.StripeSubscriptionId)
            .HasColumnName("stripe_subscription_id")
            .HasMaxLength(255);

        builder.Property(s => s.TrialEndsAt)
            .HasColumnName("trial_ends_at");

        builder.Property(s => s.CurrentPeriodStart)
            .HasColumnName("current_period_start")
            .IsRequired();

        builder.Property(s => s.CurrentPeriodEnd)
            .HasColumnName("current_period_end")
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // FK to tenants
        builder.HasOne(s => s.Tenant)
            .WithOne(t => t.Subscription)
            .HasForeignKey<Subscription>(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to plans
        builder.HasOne(s => s.Plan)
            .WithMany()
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasDatabaseName("ix_subscriptions_tenant_id");

        builder.HasIndex(s => s.StripeSubscriptionId)
            .HasDatabaseName("ix_subscriptions_stripe_subscription_id");

        // Ignore domain events collection
        builder.Ignore(s => s.DomainEvents);
    }
}
