using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Nota de Crédito document type.
/// Orchestrates nota-de-crédito-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation.
/// </summary>
public class NotaCreditoStrategy : IDocumentTypeStrategy
{
    private readonly NotaCreditoXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Valid IVA tax rates for Ecuador (2026): 0%, 5%, 12%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 12m, 15m];

    /// <summary>
    /// Expected format for numDocModificado: 001-001-000000001.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NumDocModificadoRegex =
        new(@"^\d{3}-\d{3}-\d{9}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public DocumentType DocumentType => DocumentType.NotaCredito;

    public NotaCreditoStrategy(
        NotaCreditoXmlBuilder xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Validates nota-de-crédito-specific business rules.
    /// Checks ALL fields that NotaCreditoXmlBuilder requires, using NotaCreditoConstants
    /// for alignment between validation and XML generation.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a NotaCredito
        if (document.DocumentType != DocumentType.NotaCredito)
        {
            errors.Add($"Expected document type NotaCredito, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Must have at least one item
        if (document.Items.Count == 0)
        {
            errors.Add("Nota de Crédito must have at least one line item.");
        }

        // Validate caller-provided issuer required fields
        foreach (var field in NotaCreditoConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate document sustento fields
        foreach (var field in NotaCreditoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required for Nota de Crédito.");
            }
        }

        // Validate codDocModificado must be "01" (Factura)
        if (document.IssuerInfo.TryGetValue("codDocModificado", out var codDocModificado)
            && !string.IsNullOrWhiteSpace(codDocModificado))
        {
            if (!NotaCreditoConstants.ValidDocModificadoCodes.Contains(codDocModificado))
            {
                errors.Add(
                    $"codDocModificado '{codDocModificado}' is not supported. " +
                    $"Valid values: {string.Join(", ", NotaCreditoConstants.ValidDocModificadoCodes)}.");
            }
        }

        // Validate numDocModificado format: 001-001-000000001
        if (document.IssuerInfo.TryGetValue("numDocModificado", out var numDocModificado)
            && !string.IsNullOrWhiteSpace(numDocModificado))
        {
            if (!NumDocModificadoRegex.IsMatch(numDocModificado))
            {
                errors.Add(
                    $"numDocModificado '{numDocModificado}' has invalid format. " +
                    "Expected format: ###-###-#########  (e.g. 001-001-000000001).");
            }
        }

        // Validate fechaEmisionDocSustento is parseable as dd/MM/yyyy
        if (document.IssuerInfo.TryGetValue("fechaEmisionDocSustento", out var fechaDocSustento)
            && !string.IsNullOrWhiteSpace(fechaDocSustento))
        {
            if (!DateTime.TryParseExact(fechaDocSustento, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                errors.Add(
                    $"fechaEmisionDocSustento '{fechaDocSustento}' has invalid format. " +
                    "Expected format: dd/MM/yyyy (e.g. 01/03/2026).");
            }
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

        // Validate buyer required fields
        foreach (var field in NotaCreditoConstants.RequiredBuyerFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Buyer field '{field}' is required.");
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
    /// Generates RIDE PDF for Nota de Crédito by delegating to the shared RideGenerator.
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
