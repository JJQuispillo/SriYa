using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Liquidación de Compra document type.
/// Orchestrates liquidación-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation.
/// </summary>
public class LiquidacionCompraStrategy : IDocumentTypeStrategy
{
    private readonly LiquidacionCompraXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Valid IVA tax rates for Ecuador (2026): 0%, 5%, 12%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 12m, 15m];

    public DocumentType DocumentType => DocumentType.LiquidacionCompra;

    public LiquidacionCompraStrategy(
        LiquidacionCompraXmlBuilder xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Validates liquidación de compra-specific business rules.
    /// Checks ALL fields that LiquidacionCompraXmlBuilder requires, using LiquidacionCompraConstants
    /// for alignment between validation and XML generation.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a LiquidacionCompra
        if (document.DocumentType != DocumentType.LiquidacionCompra)
        {
            errors.Add($"Expected document type LiquidacionCompra, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Must have at least one item
        if (document.Items.Count == 0)
        {
            errors.Add("LiquidacionCompra must have at least one line item.");
        }

        // Validate caller-provided issuer required fields
        foreach (var field in LiquidacionCompraConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate provider fields in BuyerInfo
        foreach (var field in LiquidacionCompraConstants.RequiredProviderFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Provider field '{field}' is required.");
            }
        }

        // Validate provider identification type
        if (document.BuyerInfo.TryGetValue("tipoIdentificacionProveedor", out var tipoId)
            && !string.IsNullOrWhiteSpace(tipoId)
            && !LiquidacionCompraConstants.ValidProviderIdTypes.Contains(tipoId))
        {
            errors.Add(
                $"Provider identification type '{tipoId}' is not valid. " +
                $"Valid types: {string.Join(", ", LiquidacionCompraConstants.ValidProviderIdTypes.OrderBy(t => t))}.");
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

        // Validate retention fields: if codigoRetencion is present, all retention fields must be present
        if (document.IssuerInfo.TryGetValue("codigoRetencion", out var codigoRetencion)
            && !string.IsNullOrWhiteSpace(codigoRetencion))
        {
            foreach (var retentionField in LiquidacionCompraConstants.RetentionFields)
            {
                if (!document.IssuerInfo.TryGetValue(retentionField, out var retVal)
                    || string.IsNullOrWhiteSpace(retVal))
                {
                    errors.Add($"Retention field '{retentionField}' is required when 'codigoRetencion' is present.");
                }
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    /// <summary>
    /// Auto-generates system fields (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// into the document's IssuerInfo, then delegates XML building to the injected builder.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Generates RIDE PDF for Liquidación de Compra by delegating to the shared RideGenerator.
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
