using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Domain.Entities;

public class Subscription : BaseEntity
{
    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }
    public DateTime? TrialEndsAt { get; private set; }
    public DateTime CurrentPeriodStart { get; private set; }
    public DateTime CurrentPeriodEnd { get; private set; }

    // Navigation properties
    public Plan Plan { get; private set; } = null!;
    public Tenant Tenant { get; private set; } = null!;

    private Subscription() { } // EF Core

    public static Subscription Create(
        Guid tenantId,
        Guid planId,
        SubscriptionStatus initialStatus = SubscriptionStatus.Trial,
        DateTime? trialEndsAt = null)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            TenantId = tenantId,
            PlanId = planId,
            Status = initialStatus,
            TrialEndsAt = trialEndsAt ?? now.AddDays(14),
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddDays(14)
        };
    }

    public void Activate(string stripeSubscriptionId, DateTime periodStart, DateTime periodEnd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeSubscriptionId);

        StripeSubscriptionId = stripeSubscriptionId;
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
        Status = SubscriptionStatus.Active;
        SetUpdatedAt();
    }

    public void SetStripeCustomerId(string stripeCustomerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeCustomerId);
        StripeCustomerId = stripeCustomerId;
        SetUpdatedAt();
    }

    public void Suspend()
    {
        Status = SubscriptionStatus.Suspended;
        SetUpdatedAt();
    }

    public void Cancel()
    {
        Status = SubscriptionStatus.Cancelled;
        SetUpdatedAt();
    }

    public void SetPastDue()
    {
        Status = SubscriptionStatus.PastDue;
        SetUpdatedAt();
    }

    public void RenewPeriod(DateTime periodStart, DateTime periodEnd)
    {
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
        Status = SubscriptionStatus.Active;
        SetUpdatedAt();
    }
}
