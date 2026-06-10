namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Tabla de referencia de los códigos de impuesto oficiales del SRI (Ecuador) usados en facturación electrónica.
/// Mapea pares (TaxTypeCode, PercentageCode) a sus tarifas de impuesto y descripciones reales.
/// </summary>
public class SriTaxCode
{
    /// <summary>
    /// Tipo impuesto: "1"=Renta, "2"=IVA, "3"=ICE, "5"=IRBPNR, "6"=ISD
    /// </summary>
    public string TaxTypeCode { get; private set; } = string.Empty;

    /// <summary>
    /// codigoPorcentaje — código de porcentaje oficial del SRI, forma la PK compuesta junto con TaxTypeCode.
    /// </summary>
    public string PercentageCode { get; private set; } = string.Empty;

    /// <summary>
    /// Porcentaje real de la tarifa de impuesto (p. ej. 15 para IVA 15%). Cero para "No Objeto" y "Exento".
    /// </summary>
    public decimal Rate { get; private set; }

    /// <summary>
    /// Descripción legible del código de impuesto.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Indica si este código de impuesto está vigente actualmente. Los códigos históricos se marcan como false.
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
