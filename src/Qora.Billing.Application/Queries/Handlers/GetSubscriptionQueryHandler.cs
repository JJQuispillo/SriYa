using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetSubscriptionQueryHandler : IRequestHandler<GetSubscriptionQuery, SubscriptionDto>
{
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IUsageRecordRepository _usageRecordRepository;

    public GetSubscriptionQueryHandler(
        ISubscriptionRepository subscriptionRepository,
        IUsageRecordRepository usageRecordRepository)
    {
        _subscriptionRepository = subscriptionRepository;
        _usageRecordRepository = usageRecordRepository;
    }

    public async Task<SubscriptionDto> Handle(GetSubscriptionQuery query, CancellationToken cancellationToken)
    {
        var subscription = await _subscriptionRepository.GetByTenantIdAsync(query.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"No se encontró una suscripción para el tenant {query.TenantId}.");

        // Billing period format: "YYYY-MM" (matches UsageRecord.BillingPeriod)
        var currentBillingPeriod = DateTime.UtcNow.ToString("yyyy-MM");
        var usedThisMonth = await _usageRecordRepository.CountByTenantAndPeriodAsync(
            query.TenantId, currentBillingPeriod, cancellationToken);

        var documentLimit = subscription.Plan?.DocumentLimit ?? -1;
        var planName = subscription.Plan?.Name ?? "Unknown";
        var priceMonthlyUsd = subscription.Plan?.PriceMonthlyUsd ?? 0m;

        // -1 means unlimited
        var documentsRemaining = documentLimit == -1
            ? -1
            : Math.Max(0, documentLimit - usedThisMonth);

        return new SubscriptionDto(
            planName,
            documentLimit,
            priceMonthlyUsd,
            subscription.Status.ToString(),
            subscription.TrialEndsAt,
            subscription.CurrentPeriodEnd,
            usedThisMonth,
            documentsRemaining);
    }
}
