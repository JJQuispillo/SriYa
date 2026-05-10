namespace Qora.Billing.Application.Settings;

/// <summary>
/// Feature flags for the Qora Billing application.
/// Bound from the "Features" configuration section.
/// </summary>
public class FeaturesSettings
{
    public const string SectionName = "Features";

    /// <summary>
    /// When true, quota enforcement is active for all tenants with a subscription.
    /// When false, documents can be processed without checking plan limits (grandfathering mode).
    /// </summary>
    public bool QuotaEnforcementEnabled { get; set; } = false;
}
