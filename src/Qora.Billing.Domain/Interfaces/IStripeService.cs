namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Represents a parsed Stripe webhook event. Defined in Domain to avoid coupling to Stripe SDK.
/// </summary>
public record StripeWebhookEvent(
    string EventType,
    string? SubscriptionId,
    string? CustomerId,
    string? SessionId,
    DateTime PeriodStart,
    DateTime PeriodEnd);

public interface IStripeService
{
    /// <summary>
    /// Creates a Stripe Checkout session for the given tenant and plan.
    /// Returns the checkout URL to redirect the user to.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(
        Guid tenantId,
        Guid planId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves Stripe subscription details by Stripe subscription ID.
    /// Returns (customerId, periodStart, periodEnd).
    /// </summary>
    Task<(string CustomerId, DateTime PeriodStart, DateTime PeriodEnd)> GetSubscriptionAsync(
        string stripeSubscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the Stripe webhook signature and constructs the event from the raw payload.
    /// Throws if the signature is invalid.
    /// </summary>
    StripeWebhookEvent ConstructWebhookEvent(string payload, string signature, string secret);
}
