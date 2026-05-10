using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Domain.Entities;

public class Tenant : BaseEntity
{
    public Ruc Ruc { get; private set; } = null!;
    public string BusinessName { get; private set; } = string.Empty;
    public string? TradeName { get; private set; }
    public string? ContactEmail { get; private set; }
    public bool IsActive { get; private set; }

    // ── Subscription ────────────────────────────────────────────────
    public Guid? SubscriptionId { get; private set; }
    public Subscription? Subscription { get; private set; }

    // ── Email delivery settings ──────────────────────────────────────
    public bool EmailEnabled { get; private set; } = false;
    public EmailProvider EmailProvider { get; private set; } = EmailProvider.Qora;
    public string? SmtpHost { get; private set; }
    public int? SmtpPort { get; private set; }
    public string? SmtpUser { get; private set; }
    public string? SmtpPassword { get; private set; }
    public bool UseSsl { get; private set; } = true;
    public string? SenderEmail { get; private set; }
    public string? SenderName { get; private set; }

    private Tenant() { } // EF Core

    public static Tenant Create(string ruc, string businessName, string? tradeName = null, string? contactEmail = null)
    {
        var tenant = new Tenant
        {
            Ruc = new Ruc(ruc),
            BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName)),
            TradeName = tradeName,
            ContactEmail = contactEmail,
            IsActive = true
        };

        return tenant;
    }

    public void SetSubscription(Guid subscriptionId)
    {
        SubscriptionId = subscriptionId;
        SetUpdatedAt();
    }

    public void Update(string businessName, string? tradeName)
    {
        BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName));
        TradeName = tradeName;
        SetUpdatedAt();
    }

    public void Deactivate()
    {
        IsActive = false;
        SetUpdatedAt();
    }

    public void Activate()
    {
        IsActive = true;
        SetUpdatedAt();
    }

    public void EnsureActive()
    {
        if (!IsActive)
            throw new TenantInactiveException(Id);
    }

    /// <summary>
    /// Enforces quota and subscription status before processing a document.
    /// No-op when quotaEnforcementEnabled is false (grandfathered tenants).
    /// </summary>
    /// <param name="currentMonthUsage">Number of documents processed this month.</param>
    /// <param name="planLimit">Plan limit (-1 = unlimited).</param>
    /// <param name="quotaEnforcementEnabled">Feature flag; skip all checks when false.</param>
    public void EnsureCanProcessDocument(int currentMonthUsage, int planLimit, bool quotaEnforcementEnabled)
    {
        if (!quotaEnforcementEnabled)
            return;

        if (Subscription is not null &&
            Subscription.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            throw new SubscriptionBlockedException(Id);
        }

        if (planLimit != -1 && currentMonthUsage >= planLimit)
        {
            var planName = Subscription?.Plan?.Name ?? "unknown";
            throw new QuotaExceededException(Id, planName, planLimit);
        }
    }

    /// <summary>
    /// Updates the tenant's email delivery configuration.
    /// Pass null for SMTP fields to leave them unchanged when only toggling enabled/provider.
    /// </summary>
    public void ConfigureEmail(
        bool emailEnabled,
        EmailProvider emailProvider,
        string? smtpHost = null,
        int? smtpPort = null,
        string? smtpUser = null,
        string? smtpPassword = null,
        bool useSsl = true,
        string? senderEmail = null,
        string? senderName = null)
    {
        EmailEnabled = emailEnabled;
        EmailProvider = emailProvider;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpUser = smtpUser;
        if (smtpPassword != null) SmtpPassword = smtpPassword;
        UseSsl = useSsl;
        SenderEmail = senderEmail;
        SenderName = senderName;
        SetUpdatedAt();
    }
}
