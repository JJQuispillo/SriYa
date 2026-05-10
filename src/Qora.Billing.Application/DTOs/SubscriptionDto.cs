namespace Qora.Billing.Application.DTOs;

public record SubscriptionDto(
    string PlanName,
    int DocumentLimit,
    decimal PriceMonthlyUsd,
    string Status,
    DateTime? TrialEndsAt,
    DateTime CurrentPeriodEnd,
    int DocumentsUsedThisMonth,
    int DocumentsRemainingThisMonth);
