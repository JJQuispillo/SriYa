using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.UnitTests.Domain;

public class TenantSubscriptionTests
{
    private static Tenant CreateTenant() =>
        Tenant.Create("1792268071001", "Test Corp");

    private static Subscription CreateSubscriptionWithStatus(SubscriptionStatus status, Guid tenantId, Guid planId)
    {
        var sub = Subscription.Create(tenantId, planId, SubscriptionStatus.Trial);

        return status switch
        {
            SubscriptionStatus.Active => ActivateSubscription(sub),
            SubscriptionStatus.Suspended => SuspendSubscription(sub),
            SubscriptionStatus.Cancelled => CancelSubscription(sub),
            SubscriptionStatus.PastDue => SetPastDueSubscription(sub),
            _ => sub
        };
    }

    private static Subscription ActivateSubscription(Subscription sub)
    {
        sub.Activate("stripe_sub_123", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1));
        return sub;
    }

    private static Subscription SuspendSubscription(Subscription sub)
    {
        sub.Suspend();
        return sub;
    }

    private static Subscription CancelSubscription(Subscription sub)
    {
        sub.Cancel();
        return sub;
    }

    private static Subscription SetPastDueSubscription(Subscription sub)
    {
        sub.SetPastDue();
        return sub;
    }

    // ── Helper to inject subscription into tenant via reflection ─────────────
    private static void InjectSubscription(Tenant tenant, Subscription subscription)
    {
        var subscriptionProp = typeof(Tenant).GetProperty("Subscription",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        subscriptionProp!.SetValue(tenant, subscription);
    }

    [Fact]
    public void EnsureCanProcessDocument_WhenQuotaEnforcementDisabled_ShouldNotThrow()
    {
        var tenant = CreateTenant();

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 999,
            planLimit: 10,
            quotaEnforcementEnabled: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureCanProcessDocument_ActiveSubscription_UnderLimit_ShouldNotThrow()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Active, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 30,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureCanProcessDocument_ActiveSubscription_UnlimitedPlan_ShouldNotThrow()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Active, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 9999,
            planLimit: -1,
            quotaEnforcementEnabled: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureCanProcessDocument_ActiveSubscription_AtLimit_ShouldThrowQuotaExceededException()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Active, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 50,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().Throw<QuotaExceededException>()
            .Which.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public void EnsureCanProcessDocument_ActiveSubscription_OverLimit_ShouldThrowQuotaExceededException()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Active, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 51,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().Throw<QuotaExceededException>();
    }

    [Fact]
    public void EnsureCanProcessDocument_SuspendedSubscription_ShouldThrowSubscriptionBlockedException()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Suspended, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 0,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().Throw<SubscriptionBlockedException>()
            .Which.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public void EnsureCanProcessDocument_CancelledSubscription_ShouldThrowSubscriptionBlockedException()
    {
        var tenant = CreateTenant();
        var planId = Guid.NewGuid();
        var subscription = CreateSubscriptionWithStatus(SubscriptionStatus.Cancelled, tenant.Id, planId);
        InjectSubscription(tenant, subscription);

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 0,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().Throw<SubscriptionBlockedException>()
            .Which.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public void EnsureCanProcessDocument_NoSubscription_WithinLimit_ShouldNotThrow()
    {
        var tenant = CreateTenant();
        // No subscription injected — Subscription is null

        var act = () => tenant.EnsureCanProcessDocument(
            currentMonthUsage: 5,
            planLimit: 50,
            quotaEnforcementEnabled: true);

        act.Should().NotThrow();
    }
}
