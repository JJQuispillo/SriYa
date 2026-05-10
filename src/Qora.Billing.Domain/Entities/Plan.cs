namespace Qora.Billing.Domain.Entities;

public class Plan : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;

    /// <summary>
    /// Monthly document limit. -1 means unlimited.
    /// </summary>
    public int DocumentLimit { get; private set; }

    public decimal PriceMonthlyUsd { get; private set; }
    public string? StripeProductId { get; private set; }
    public string? StripePriceId { get; private set; }
    public bool IsActive { get; private set; }

    private Plan() { } // EF Core

    public static Plan Create(
        string name,
        string slug,
        int documentLimit,
        decimal priceMonthlyUsd,
        string? stripeProductId = null,
        string? stripePriceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return new Plan
        {
            Name = name,
            Slug = slug,
            DocumentLimit = documentLimit,
            PriceMonthlyUsd = priceMonthlyUsd,
            StripeProductId = stripeProductId,
            StripePriceId = stripePriceId,
            IsActive = true
        };
    }

    public void UpdateStripeIds(string stripeProductId, string stripePriceId)
    {
        StripeProductId = stripeProductId;
        StripePriceId = stripePriceId;
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
}
