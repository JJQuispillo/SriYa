using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Comprobante de Retención document type.
/// Orchestrates retention-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation.
///
/// DocumentItem field usage:
///   item.TaxCode              → validated against ValidTaxCodes ("1"=Renta, "2"=IVA, "6"=ISD)
///   item.TaxPercentageCode    → SRI retention percentage code (passed through to XML)
///   item.TaxRate              → retention percentage, must be > 0
///   item.SustentoDocumentType → SRI code for the type of sustento document (required)
///   item.SustentoDocumentNumber → number of the sustento document (required)
/// </summary>
public class ComprobanteRetencionStrategy : IDocumentTypeStrategy
{
    private readonly ComprobanteRetencionXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Regex pattern for periodoFiscal: MM/YYYY format.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PeriodoFiscalRegex =
        new(@"^\d{2}/\d{4}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public DocumentType DocumentType => DocumentType.ComprobanteRetencion;

    public ComprobanteRetencionStrategy(
        ComprobanteRetencionXmlBuilder xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Validates comprobante de retención-specific business rules.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a ComprobanteRetencion
        if (document.DocumentType != DocumentType.ComprobanteRetencion)
        {
            errors.Add($"Expected document type ComprobanteRetencion, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Must have at least one retention line
        if (document.Items.Count == 0)
        {
            errors.Add("Comprobante de Retención must have at least one retention line (impuesto).");
        }

        // Validate caller-provided issuer required fields
        foreach (var field in ComprobanteRetencionConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate periodoFiscal format: MM/YYYY
        if (document.IssuerInfo.TryGetValue("periodoFiscal", out var periodoFiscal)
            && !string.IsNullOrWhiteSpace(periodoFiscal))
        {
            if (!PeriodoFiscalRegex.IsMatch(periodoFiscal))
            {
                errors.Add(
                    $"Issuer field 'periodoFiscal' must be in MM/YYYY format (e.g., '01/2026'), got '{periodoFiscal}'.");
            }
        }

        // Validate buyer (sujeto retenido) required fields
        foreach (var field in ComprobanteRetencionConstants.RequiredBuyerFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Buyer field '{field}' is required.");
            }
        }

        // Validate each retention line item
        foreach (var item in document.Items)
        {
            // TaxCode (item.TaxCode) must be a valid SRI retention tax type
            if (!ComprobanteRetencionConstants.ValidTaxCodes.Contains(item.TaxCode))
            {
                errors.Add(
                    $"Item '{item.Description}' has invalid tax type code '{item.TaxCode}'. " +
                    $"Valid codes: {string.Join(", ", ComprobanteRetencionConstants.ValidTaxCodes)} " +
                    "(1=Renta, 2=IVA, 6=ISD).");
            }

            // TaxRate (retention percentage) must be positive
            if (item.TaxRate <= 0)
            {
                errors.Add(
                    $"Item '{item.Description}' has invalid retention rate {item.TaxRate}%. " +
                    "Rate must be greater than zero.");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    /// <summary>
    /// Auto-generates system fields (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// into the document's IssuerInfo, then delegates XML building to the injected ComprobanteRetencionXmlBuilder.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        // Validate that every retention line has the required sustento fields
        foreach (var item in document.Items)
        {
            if (string.IsNullOrWhiteSpace(item.SustentoDocumentType))
            {
                throw new InvalidOperationException(
                    $"El ítem '{item.Description}' no tiene el campo SustentoDocumentType requerido para ComprobanteRetencion.");
            }
        }

        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Generates RIDE PDF for Comprobante de Retención by delegating to the shared RideGenerator.
    /// </summary>
    public Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default)
    {
        return _rideGenerator.GeneratePdfAsync(document, cancellationToken);
    }

    /// <summary>
    /// Populates system-generated fields into the document's IssuerInfo dictionary
    /// before XML generation. Uses SRI configuration for environment and emission type,
    /// and AccessKeyGenerator for the 49-digit access key.
    /// Same logic as FacturaStrategy.PopulateSystemFields.
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
