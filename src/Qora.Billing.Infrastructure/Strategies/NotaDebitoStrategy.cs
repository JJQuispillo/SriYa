using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Estrategia para el tipo de documento Nota de Débito.
/// Orquesta la validación específica de la nota de débito, la autogeneración de campos del sistema,
/// la generación del XML y la generación del RIDE PDF.
/// </summary>
public class NotaDebitoStrategy : IDocumentTypeStrategy
{
    private readonly NotaDebitoXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Tarifas de IVA válidas para Ecuador (2026): 0%, 5%, 12%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 12m, 15m];

    /// <summary>
    /// Formato del número de documento del SRI: estab-ptoEmi-secuencial (ej., 001-001-000000001).
    /// </summary>
    private static readonly Regex NumDocSustentoPattern = new(@"^\d{3}-\d{3}-\d{9}$", RegexOptions.Compiled);

    /// <summary>
    /// codDoc del SRI para una Factura, que es el único documento de sustento válido para la Nota de Débito.
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
    /// Valida las reglas de negocio específicas de la nota de débito.
    /// Verifica TODOS los campos que requiere NotaDebitoXmlBuilder, usando NotaDebitoConstants
    /// para mantener la alineación entre la validación y la generación del XML.
    /// Devuelve una lista de mensajes de error de validación (vacía si es válido).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Debe ser una NotaDebito
        if (document.DocumentType != DocumentType.NotaDebito)
        {
            errors.Add($"Expected document type NotaDebito, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Debe tener al menos un motivo (ítem)
        if (document.Items.Count == 0)
        {
            errors.Add("Nota de Débito must have at least one motivo (line item).");
        }

        // Validar los campos obligatorios del emisor provistos por el llamador
        foreach (var field in NotaDebitoConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validar los campos del documento sustento
        foreach (var field in NotaDebitoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required for Nota de Débito.");
            }
        }

        // codDocSustento debe ser "01" (Factura)
        if (document.IssuerInfo.TryGetValue("codDocSustento", out var codDocSustento) &&
            !string.IsNullOrWhiteSpace(codDocSustento) &&
            codDocSustento != CodDocSustentoFactura)
        {
            errors.Add($"codDocSustento must be '{CodDocSustentoFactura}' (Factura). Got '{codDocSustento}'.");
        }

        // numDocSustento debe cumplir el formato 001-001-000000001
        if (document.IssuerInfo.TryGetValue("numDocSustento", out var numDocSustento) &&
            !string.IsNullOrWhiteSpace(numDocSustento) &&
            !NumDocSustentoPattern.IsMatch(numDocSustento))
        {
            errors.Add($"numDocSustento '{numDocSustento}' must match format '###-###-#########' (e.g., 001-001-000000001).");
        }

        // fechaEmisionDocSustento debe ser parseable como dd/MM/yyyy
        if (document.IssuerInfo.TryGetValue("fechaEmisionDocSustento", out var fechaEmision) &&
            !string.IsNullOrWhiteSpace(fechaEmision) &&
            !DateTime.TryParseExact(fechaEmision, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            errors.Add($"fechaEmisionDocSustento '{fechaEmision}' must be in format 'dd/MM/yyyy'.");
        }

        // Validar las tarifas de IVA en todos los motivos
        foreach (var item in document.Items)
        {
            if (!ValidIvaRates.Contains(item.TaxRate))
            {
                errors.Add(
                    $"Motivo '{item.Description}' has invalid IVA rate {item.TaxRate}%. " +
                    $"Valid rates for 2026: {string.Join(", ", ValidIvaRates.OrderBy(r => r).Select(r => $"{r}%"))}.");
            }
        }

        // Validar los campos obligatorios del comprador (siempre requeridos para NotaDebito)
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
    /// Autogenera los campos del sistema (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// en el IssuerInfo del documento, luego delega la construcción del XML a NotaDebitoXmlBuilder.
    /// </summary>
    public async Task<BuildXmlResult> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        var accessKey = PopulateSystemFields(document);
        var xml = await _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
        return new BuildXmlResult(xml, accessKey);
    }

    /// <summary>
    /// Genera el RIDE PDF para la Nota de Débito delegando al RideGenerator compartido.
    /// </summary>
    public Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default)
    {
        return _rideGenerator.GeneratePdfAsync(document, cancellationToken);
    }

    /// <summary>
    /// Rellena los campos generados por el sistema en el diccionario IssuerInfo del documento
    /// antes de la generación del XML. Usa la configuración del SRI para el ambiente y el tipo de emisión,
    /// y AccessKeyGenerator para la clave de acceso de 49 dígitos.
    /// </summary>
    private Domain.ValueObjects.AccessKey PopulateSystemFields(Document document)
    {
        var issuer = document.IssuerInfo;
        var now = DateTime.UtcNow;

        // Determinar el ambiente desde la configuración del SRI
        var environment = _sriConfiguration.Environment;

        // ambiente: 1=Test, 2=Production
        issuer["ambiente"] = ((int)environment).ToString();

        // tipoEmision: siempre Normal (1) para la emisión estándar
        issuer["tipoEmision"] = ((int)EmissionType.Normal).ToString();

        // fechaEmision: fecha actual en formato dd/MM/yyyy (formato del SRI)
        issuer["fechaEmision"] = now.ToString("dd/MM/yyyy");

        // claveAcceso: clave de acceso de 49 dígitos generada con AccessKeyGenerator
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

        return accessKey;
    }
}
