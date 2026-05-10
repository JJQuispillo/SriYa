using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class HandleStripeWebhookCommandHandler : IRequestHandler<HandleStripeWebhookCommand>
{
    private readonly IStripeService _stripeService;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly StripeSettings _stripeSettings;
    private readonly ILogger<HandleStripeWebhookCommandHandler> _logger;

    public HandleStripeWebhookCommandHandler(
        IStripeService stripeService,
        ISubscriptionRepository subscriptionRepository,
        IUnitOfWork unitOfWork,
        IOptions<StripeSettings> stripeSettings,
        ILogger<HandleStripeWebhookCommandHandler> logger)
    {
        _stripeService = stripeService;
        _subscriptionRepository = subscriptionRepository;
        _unitOfWork = unitOfWork;
        _stripeSettings = stripeSettings.Value;
        _logger = logger;
    }

    public async Task Handle(HandleStripeWebhookCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_stripeSettings.WebhookSecret))
            throw new BillingDomainException("Stripe webhook secret no configurado.");

        // Validate signature and parse event — throws on invalid HMAC
        var webhookEvent = _stripeService.ConstructWebhookEvent(
            command.Payload,
            command.StripeSignature,
            _stripeSettings.WebhookSecret);

        _logger.LogInformation("Processing Stripe webhook event: {EventType}", webhookEvent.EventType);

        switch (webhookEvent.EventType)
        {
            case "checkout.session.completed":
                await HandleCheckoutSessionCompletedAsync(webhookEvent, cancellationToken);
                break;

            case "invoice.payment_succeeded":
                await HandleInvoicePaymentSucceededAsync(webhookEvent, cancellationToken);
                break;

            case "invoice.payment_failed":
                await HandleInvoicePaymentFailedAsync(webhookEvent, cancellationToken);
                break;

            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(webhookEvent, cancellationToken);
                break;

            default:
                _logger.LogInformation(
                    "Unhandled Stripe event type '{EventType}' — skipping.",
                    webhookEvent.EventType);
                break;
        }
    }

    private async Task HandleCheckoutSessionCompletedAsync(
        StripeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.SubscriptionId))
        {
            _logger.LogWarning("checkout.session.completed event missing SubscriptionId — skipping.");
            return;
        }

        // Get the full subscription details from Stripe to obtain the billing period
        var (_, periodStart, periodEnd) = await _stripeService.GetSubscriptionAsync(
            webhookEvent.SubscriptionId, cancellationToken);

        // Find the subscription by StripeSubscriptionId (idempotency: already processed?)
        var subscription = await _subscriptionRepository.GetByStripeSubscriptionIdAsync(
            webhookEvent.SubscriptionId, cancellationToken);

        if (subscription is not null && subscription.Status == Domain.Enums.SubscriptionStatus.Active)
        {
            _logger.LogInformation(
                "Subscription {StripeSubId} already Active — checkout.session.completed is a no-op.",
                webhookEvent.SubscriptionId);
            return;
        }

        if (subscription is null)
        {
            _logger.LogWarning(
                "No subscription record found for checkout.session.completed (StripeSubId={StripeSubId}).",
                webhookEvent.SubscriptionId);
            return;
        }

        subscription.Activate(webhookEvent.SubscriptionId, periodStart, periodEnd);
        await _subscriptionRepository.UpdateAsync(subscription, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Subscription {SubId} activated via checkout.session.completed, period {Start}→{End}.",
            subscription.Id, periodStart, periodEnd);
    }

    private async Task HandleInvoicePaymentSucceededAsync(
        StripeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.SubscriptionId))
        {
            _logger.LogWarning("invoice.payment_succeeded event missing SubscriptionId — skipping.");
            return;
        }

        var subscription = await _subscriptionRepository.GetByStripeSubscriptionIdAsync(
            webhookEvent.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            _logger.LogWarning(
                "No subscription found for StripeSubscriptionId {StripeSubId} on invoice.payment_succeeded.",
                webhookEvent.SubscriptionId);
            return;
        }

        // Idempotency: if already Active for this period, no-op
        if (subscription.Status == Domain.Enums.SubscriptionStatus.Active
            && subscription.CurrentPeriodEnd == webhookEvent.PeriodEnd)
        {
            _logger.LogInformation(
                "Subscription {SubId} already Active for period ending {End} — invoice.payment_succeeded is a no-op.",
                subscription.Id, webhookEvent.PeriodEnd);
            return;
        }

        subscription.RenewPeriod(webhookEvent.PeriodStart, webhookEvent.PeriodEnd);
        await _subscriptionRepository.UpdateAsync(subscription, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Subscription {SubId} renewed via invoice.payment_succeeded, period {Start}→{End}.",
            subscription.Id, webhookEvent.PeriodStart, webhookEvent.PeriodEnd);
    }

    private async Task HandleInvoicePaymentFailedAsync(
        StripeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.SubscriptionId))
        {
            _logger.LogWarning("invoice.payment_failed event missing SubscriptionId — skipping.");
            return;
        }

        var subscription = await _subscriptionRepository.GetByStripeSubscriptionIdAsync(
            webhookEvent.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            _logger.LogWarning(
                "No subscription found for StripeSubscriptionId {StripeSubId} on invoice.payment_failed.",
                webhookEvent.SubscriptionId);
            return;
        }

        // Idempotency: already PastDue, no-op
        if (subscription.Status == Domain.Enums.SubscriptionStatus.PastDue)
        {
            _logger.LogInformation(
                "Subscription {SubId} already PastDue — invoice.payment_failed is a no-op.",
                subscription.Id);
            return;
        }

        subscription.SetPastDue();
        await _subscriptionRepository.UpdateAsync(subscription, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Subscription {SubId} set to PastDue via invoice.payment_failed.",
            subscription.Id);
    }

    private async Task HandleSubscriptionDeletedAsync(
        StripeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.SubscriptionId))
        {
            _logger.LogWarning("customer.subscription.deleted event missing SubscriptionId — skipping.");
            return;
        }

        var subscription = await _subscriptionRepository.GetByStripeSubscriptionIdAsync(
            webhookEvent.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            _logger.LogWarning(
                "No subscription found for StripeSubscriptionId {StripeSubId} on customer.subscription.deleted.",
                webhookEvent.SubscriptionId);
            return;
        }

        // Idempotency: already Cancelled, no-op
        if (subscription.Status == Domain.Enums.SubscriptionStatus.Cancelled)
        {
            _logger.LogInformation(
                "Subscription {SubId} already Cancelled — customer.subscription.deleted is a no-op.",
                subscription.Id);
            return;
        }

        subscription.Cancel();
        await _subscriptionRepository.UpdateAsync(subscription, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Subscription {SubId} cancelled via customer.subscription.deleted.",
            subscription.Id);
    }
}
