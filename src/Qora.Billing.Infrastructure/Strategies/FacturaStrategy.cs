using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Factura (invoice) document type.
/// Orchestrates factura-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation.
/// </summary>
public class FacturaStrategy : IDocumentTypeStrategy
{
    private readonly IXmlGenerator _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Valid IVA tax rates for Ecuador (2026): 0%, 5%, 12%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 12m, 15m];

    /// <summary>
    /// Threshold amount (USD) above which buyer identification is mandatory.
    /// </summary>
    private const decimal BuyerInfoRequiredThreshold = 200m;

    public DocumentType DocumentType => DocumentType.Factura;

    public FacturaStrategy(
        IXmlGenerator xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Validates factura-specific business rules.
    /// Checks ALL fields that FacturaXmlBuilder requires, using FacturaConstants
    /// for alignment between validation and XML generation.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a Factura
        if (document.DocumentType != DocumentType.Factura)
        {
            errors.Add($"Expected document type Factura, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Must have at least one item
        if (document.Items.Count == 0)
        {
            errors.Add("Factura must have at least one line item.");
        }

        // Validate IVA rates
        foreach (var item in document.Items)
        {
            if (!ValidIvaRates.Contains(item.TaxRate))
            {
                errors.Add(
                    $"Item '{item.Description}' has invalid IVA rate {item.TaxRate}%. " +
                    $"Valid rates for 2026: {string.Join(", ", ValidIvaRates.OrderBy(r => r).Select(r => $"{r}%"))}.");
            }
        }

        // Validate ALL caller-provided issuer required fields (using FacturaConstants)
        foreach (var field in FacturaConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate buyer info required for amounts > $200
        var totalAmount = document.Items.Sum(i => i.Subtotal + (i.Subtotal * i.TaxRate / 100m));
        if (totalAmount > BuyerInfoRequiredThreshold)
        {
            foreach (var field in FacturaConstants.RequiredBuyerFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    errors.Add(
                        $"Buyer field '{field}' is required for invoices exceeding ${BuyerInfoRequiredThreshold}. " +
                        $"Total: ${totalAmount:F2}.");
                }
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    /// <summary>
    /// Auto-generates system fields (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// into the document's IssuerInfo, then delegates XML building to the injected FacturaXmlBuilder.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Delegates RIDE PDF building to the injected IRideGenerator.
    /// </summary>
    public Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default)
    {
        return _rideGenerator.GeneratePdfAsync(document, cancellationToken);
    }

    /// <summary>
    /// Populates system-generated fields into the document's IssuerInfo dictionary
    /// before XML generation. Uses SRI configuration for environment and emission type,
    /// and AccessKeyGenerator for the 49-digit access key.
    /// </summary>
    private void PopulateSystemFields(Document document)
    {
        var issuer = document.IssuerInfo;
        var now = DateTime.UtcNow;

        // Determine environment from SRI configuration
        var environment = _sriConfiguration.Environment;

        // ambiente: 1=Test, 2=Production
        issuer["ambiente"] = ((int)environment).ToString();

        // tipoEmision: always Normal (1) for standard emission
        issuer["tipoEmision"] = ((int)EmissionType.Normal).ToString();

        // fechaEmision: current date in dd/MM/yyyy format (SRI format)
        issuer["fechaEmision"] = now.ToString("dd/MM/yyyy");

        // claveAcceso: 49-digit access key generated using AccessKeyGenerator
        var numericCode = AccessKeyGenerator.GenerateNumericCode();
        var accessKey = AccessKeyGenerator.Generate(
            issueDate: now,
            documentType: document.DocumentType,
            ruc: issuer["ruc"],
            environment: environment,
            establishment: issuer["estab"],
            emissionPoint: issuer["ptoEmi"],
            sequential: issuer["secuencial"],
            numericCode: numericCode,
            emissionType: EmissionType.Normal);

        issuer["claveAcceso"] = accessKey.Value;
    }
}
