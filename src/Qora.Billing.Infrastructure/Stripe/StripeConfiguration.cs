namespace Qora.Billing.Infrastructure.Stripe;

/// <summary>
/// Configuration settings for the Stripe integration.
/// Bound from the "Stripe" section in appsettings.json.
/// </summary>
public class StripeConfiguration
{
    public const string SectionName = "Stripe";

    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string FreePlanPriceId { get; set; } = string.Empty;
    public string BasicPlanPriceId { get; set; } = string.Empty;
    public string ProPlanPriceId { get; set; } = string.Empty;
    public string SuccessUrl { get; set; } = "https://app.qora.app/subscription/success";
    public string CancelUrl { get; set; } = "https://app.qora.app/subscription/cancel";
}
