using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace Qora.Billing.Infrastructure.Stripe;

/// <summary>
/// Implements IStripeService using the official Stripe.net SDK (v51+).
/// Handles checkout session creation, subscription retrieval, and webhook signature validation.
///
/// API notes for Stripe.net v51:
///   - Subscription period is on SubscriptionItem.CurrentPeriodStart/End (not on Subscription itself)
///   - Invoice.SubscriptionId is on Invoice.Parent.SubscriptionDetails.SubscriptionId
/// </summary>
public class StripeService : IStripeService
{
    private readonly StripeConfiguration _config;
    private readonly ILogger<StripeService> _logger;

    public StripeService(IOptions<StripeConfiguration> options, ILogger<StripeService> logger)
    {
        _config = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> CreateCheckoutSessionAsync(
        Guid tenantId,
        Guid planId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var service = new SessionService();

        var sessionOptions = new SessionCreateOptions
        {
            Mode = "subscription",
            CustomerEmail = email,
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Price = ResolvePriceId(planId),
                    Quantity = 1
                }
            ],
            SuccessUrl = _config.SuccessUrl + "?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = _config.CancelUrl,
            Metadata = new Dictionary<string, string>
            {
                ["tenantId"] = tenantId.ToString(),
                ["planId"]   = planId.ToString()
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["planId"]   = planId.ToString()
                }
            }
        };

        _logger.LogInformation(
            "Creating Stripe checkout session for tenant {TenantId}, plan {PlanId}",
            tenantId, planId);

        var session = await service.CreateAsync(sessionOptions, cancellationToken: cancellationToken);

        return session.Url
               ?? throw new InvalidOperationException("Stripe returned a checkout session with no URL.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// In Stripe.net v51, period information lives on SubscriptionItem.CurrentPeriodStart/End.
    /// We take the first item's period as the subscription-level period.
    /// </remarks>
    public async Task<(string CustomerId, DateTime PeriodStart, DateTime PeriodEnd)> GetSubscriptionAsync(
        string stripeSubscriptionId,
        CancellationToken cancellationToken = default)
    {
        var service = new SubscriptionService();
        var getOptions = new SubscriptionGetOptions
        {
            Expand = ["items"]
        };
        var subscription = await service.GetAsync(stripeSubscriptionId, getOptions, cancellationToken: cancellationToken);

        // Period is on the first subscription item in Stripe.net v51
        var firstItem = subscription.Items?.Data?.FirstOrDefault();
        var periodStart = firstItem?.CurrentPeriodStart ?? subscription.StartDate;
        var periodEnd   = firstItem?.CurrentPeriodEnd   ?? subscription.TrialEnd ?? subscription.StartDate.AddMonths(1);

        return (subscription.CustomerId, periodStart, periodEnd);
    }

    /// <inheritdoc />
    public StripeWebhookEvent ConstructWebhookEvent(string payload, string signature, string secret)
    {
        var stripeEvent = EventUtility.ConstructEvent(payload, signature, secret);

        _logger.LogInformation("Received Stripe webhook event: {EventType}", stripeEvent.Type);

        return stripeEvent.Type switch
        {
            "checkout.session.completed"    => MapCheckoutSessionCompleted(stripeEvent),
            "customer.subscription.updated" => MapSubscriptionUpdated(stripeEvent),
            "customer.subscription.deleted" => MapSubscriptionDeleted(stripeEvent),
            "invoice.payment_succeeded"     => MapInvoicePaymentSucceeded(stripeEvent),
            "invoice.payment_failed"        => MapInvoicePaymentFailed(stripeEvent),
            _ => new StripeWebhookEvent(
                EventType: stripeEvent.Type,
                SubscriptionId: null,
                CustomerId: null,
                SessionId: null,
                PeriodStart: DateTime.UtcNow,
                PeriodEnd: DateTime.UtcNow)
        };
    }

    // ── Private mapping helpers ──────────────────────────────────────────────

    private static StripeWebhookEvent MapCheckoutSessionCompleted(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as Session
                      ?? throw new InvalidOperationException(
                          "Expected Session object in checkout.session.completed");

        return new StripeWebhookEvent(
            EventType: stripeEvent.Type,
            SubscriptionId: session.SubscriptionId,
            CustomerId: session.CustomerId,
            SessionId: session.Id,
            PeriodStart: DateTime.UtcNow,
            PeriodEnd: DateTime.UtcNow.AddMonths(1));
    }

    private static StripeWebhookEvent MapSubscriptionUpdated(Event stripeEvent)
    {
        // In v51, Subscription (the Stripe entity) has no CurrentPeriodStart/End at the subscription level.
        // Period info is on SubscriptionItem. For webhook events the Items collection may be included.
        var sub = stripeEvent.Data.Object as global::Stripe.Subscription
                  ?? throw new InvalidOperationException(
                      "Expected Subscription object in customer.subscription.updated");

        var firstItem = sub.Items?.Data?.FirstOrDefault();
        var periodStart = firstItem?.CurrentPeriodStart ?? sub.StartDate;
        var periodEnd   = firstItem?.CurrentPeriodEnd   ?? sub.TrialEnd ?? sub.StartDate.AddMonths(1);

        return new StripeWebhookEvent(
            EventType: stripeEvent.Type,
            SubscriptionId: sub.Id,
            CustomerId: sub.CustomerId,
            SessionId: null,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd);
    }

    private static StripeWebhookEvent MapSubscriptionDeleted(Event stripeEvent)
    {
        var sub = stripeEvent.Data.Object as global::Stripe.Subscription
                  ?? throw new InvalidOperationException(
                      "Expected Subscription object in customer.subscription.deleted");

        var firstItem = sub.Items?.Data?.FirstOrDefault();
        var periodStart = firstItem?.CurrentPeriodStart ?? sub.StartDate;
        var periodEnd   = firstItem?.CurrentPeriodEnd   ?? sub.TrialEnd ?? sub.StartDate.AddMonths(1);

        return new StripeWebhookEvent(
            EventType: stripeEvent.Type,
            SubscriptionId: sub.Id,
            CustomerId: sub.CustomerId,
            SessionId: null,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd);
    }

    private static StripeWebhookEvent MapInvoicePaymentSucceeded(Event stripeEvent)
    {
        // In v51 Invoice.SubscriptionId is exposed via Invoice.Parent.SubscriptionDetails.SubscriptionId
        var invoice = stripeEvent.Data.Object as Invoice
                      ?? throw new InvalidOperationException(
                          "Expected Invoice object in invoice.payment_succeeded");

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;

        return new StripeWebhookEvent(
            EventType: stripeEvent.Type,
            SubscriptionId: subscriptionId,
            CustomerId: invoice.CustomerId,
            SessionId: null,
            PeriodStart: invoice.PeriodStart,
            PeriodEnd: invoice.PeriodEnd);
    }

    private static StripeWebhookEvent MapInvoicePaymentFailed(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as Invoice
                      ?? throw new InvalidOperationException(
                          "Expected Invoice object in invoice.payment_failed");

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;

        return new StripeWebhookEvent(
            EventType: stripeEvent.Type,
            SubscriptionId: subscriptionId,
            CustomerId: invoice.CustomerId,
            SessionId: null,
            PeriodStart: invoice.PeriodStart,
            PeriodEnd: invoice.PeriodEnd);
    }

    private string ResolvePriceId(Guid planId)
    {
        if (planId == Persistence.Configurations.PlanConfiguration.FreePlanId)  return _config.FreePlanPriceId;
        if (planId == Persistence.Configurations.PlanConfiguration.BasicPlanId) return _config.BasicPlanPriceId;
        if (planId == Persistence.Configurations.PlanConfiguration.ProPlanId)   return _config.ProPlanPriceId;

        throw new ArgumentOutOfRangeException(nameof(planId), $"No Stripe price mapped for plan {planId}");
    }
}
