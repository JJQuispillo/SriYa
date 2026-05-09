namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Reference table for SRI (Ecuador) official tax codes used in electronic invoicing.
/// Maps (TaxTypeCode, PercentageCode) pairs to their actual tax rates and descriptions.
/// </summary>
public class SriTaxCode
{
    /// <summary>
    /// Tipo impuesto: "1"=Renta, "2"=IVA, "3"=ICE, "5"=IRBPNR, "6"=ISD
    /// </summary>
    public string TaxTypeCode { get; private set; } = string.Empty;

    /// <summary>
    /// codigoPorcentaje — official SRI percentage code, forms composite PK with TaxTypeCode.
    /// </summary>
    public string PercentageCode { get; private set; } = string.Empty;

    /// <summary>
    /// Actual tax rate percentage (e.g. 15 for IVA 15%). Zero for "No Objeto" and "Exento".
    /// </summary>
    public decimal Rate { get; private set; }

    /// <summary>
    /// Human-readable description of the tax code.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Indicates whether this tax code is currently valid. Historical codes are set to false.
    /// </summary>
    public bool IsActive { get; private set; }

    private SriTaxCode() { } // EF Core

    public static SriTaxCode Create(
        string taxTypeCode,
        string percentageCode,
        decimal rate,
        string description,
        bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(taxTypeCode))
            throw new ArgumentException("TaxTypeCode es requerido.", nameof(taxTypeCode));
        if (string.IsNullOrWhiteSpace(percentageCode))
            throw new ArgumentException("PercentageCode es requerido.", nameof(percentageCode));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description es requerida.", nameof(description));
        if (rate < 0)
            throw new ArgumentException("Rate no puede ser negativo.", nameof(rate));

        return new SriTaxCode
        {
            TaxTypeCode = taxTypeCode,
            PercentageCode = percentageCode,
            Rate = rate,
            Description = description,
            IsActive = isActive
        };
    }
}
