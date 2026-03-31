using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Nota de Débito document type.
/// Orchestrates nota-de-débito-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation.
/// </summary>
public class NotaDebitoStrategy : IDocumentTypeStrategy
{
    private readonly NotaDebitoXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Valid IVA tax rates for Ecuador (2026): 0%, 5%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 15m];

    /// <summary>
    /// SRI document number format: estab-ptoEmi-secuencial (e.g., 001-001-000000001).
    /// </summary>
    private static readonly Regex NumDocSustentoPattern = new(@"^\d{3}-\d{3}-\d{9}$", RegexOptions.Compiled);

    /// <summary>
    /// SRI codDoc for a Factura, which is the only valid sustaining document for Nota de Débito.
    /// </summary>
    private const string CodDocSustentoFactura = "01";

    public DocumentType DocumentType => DocumentType.NotaDebito;

    public NotaDebitoStrategy(
        NotaDebitoXmlBuilder xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Validates nota-de-débito-specific business rules.
    /// Checks ALL fields that NotaDebitoXmlBuilder requires, using NotaDebitoConstants
    /// for alignment between validation and XML generation.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a NotaDebito
        if (document.DocumentType != DocumentType.NotaDebito)
        {
            errors.Add($"Expected document type NotaDebito, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Must have at least one motivo (item)
        if (document.Items.Count == 0)
        {
            errors.Add("Nota de Débito must have at least one motivo (line item).");
        }

        // Validate caller-provided issuer required fields
        foreach (var field in NotaDebitoConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate doc sustento fields
        foreach (var field in NotaDebitoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required for Nota de Débito.");
            }
        }

        // codDocSustento must be "01" (Factura)
        if (document.IssuerInfo.TryGetValue("codDocSustento", out var codDocSustento) &&
            !string.IsNullOrWhiteSpace(codDocSustento) &&
            codDocSustento != CodDocSustentoFactura)
        {
            errors.Add($"codDocSustento must be '{CodDocSustentoFactura}' (Factura). Got '{codDocSustento}'.");
        }

        // numDocSustento must match 001-001-000000001 format
        if (document.IssuerInfo.TryGetValue("numDocSustento", out var numDocSustento) &&
            !string.IsNullOrWhiteSpace(numDocSustento) &&
            !NumDocSustentoPattern.IsMatch(numDocSustento))
        {
            errors.Add($"numDocSustento '{numDocSustento}' must match format '###-###-#########' (e.g., 001-001-000000001).");
        }

        // fechaEmisionDocSustento must be parseable as dd/MM/yyyy
        if (document.IssuerInfo.TryGetValue("fechaEmisionDocSustento", out var fechaEmision) &&
            !string.IsNullOrWhiteSpace(fechaEmision) &&
            !DateTime.TryParseExact(fechaEmision, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            errors.Add($"fechaEmisionDocSustento '{fechaEmision}' must be in format 'dd/MM/yyyy'.");
        }

        // Validate IVA rates on all motivos
        foreach (var item in document.Items)
        {
            if (!ValidIvaRates.Contains(item.TaxRate))
            {
                errors.Add(
                    $"Motivo '{item.Description}' has invalid IVA rate {item.TaxRate}%. " +
                    $"Valid rates for 2026: {string.Join(", ", ValidIvaRates.OrderBy(r => r).Select(r => $"{r}%"))}.");
            }
        }

        // Validate buyer required fields (always required for NotaDebito)
        foreach (var field in NotaDebitoConstants.RequiredBuyerFields)
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
    /// into the document's IssuerInfo, then delegates XML building to NotaDebitoXmlBuilder.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Generates RIDE PDF for Nota de Débito by delegating to the shared RideGenerator.
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
