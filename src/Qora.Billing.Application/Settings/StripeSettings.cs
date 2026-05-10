namespace Qora.Billing.Application.Settings;

/// <summary>
/// Configuration settings for Stripe integration.
/// Bound from the "Stripe" configuration section.
/// </summary>
public class StripeSettings
{
    public const string SectionName = "Stripe";

    public string SecretKey { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}
