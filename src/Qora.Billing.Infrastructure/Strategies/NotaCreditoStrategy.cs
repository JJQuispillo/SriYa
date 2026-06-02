using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Estrategia para el tipo de documento Nota de Crédito.
/// Orquesta la validación específica de la nota de crédito, la autogeneración de campos del sistema,
/// la generación del XML y la generación del RIDE PDF.
/// </summary>
public class NotaCreditoStrategy : IDocumentTypeStrategy
{
    private readonly NotaCreditoXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Tarifas de IVA válidas para Ecuador (2026): 0%, 5%, 12%, 15%.
    /// </summary>
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 12m, 15m];

    /// <summary>
    /// Formato esperado para numDocModificado: 001-001-000000001.
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
    /// Valida las reglas de negocio específicas de la nota de crédito.
    /// Verifica TODOS los campos que requiere NotaCreditoXmlBuilder, usando NotaCreditoConstants
    /// para mantener la alineación entre la validación y la generación del XML.
    /// Devuelve una lista de mensajes de error de validación (vacía si es válido).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Debe ser una NotaCredito
        if (document.DocumentType != DocumentType.NotaCredito)
        {
            errors.Add($"Expected document type NotaCredito, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Debe tener al menos un ítem
        if (document.Items.Count == 0)
        {
            errors.Add("Nota de Crédito must have at least one line item.");
        }

        // Validar los campos obligatorios del emisor provistos por el llamador
        foreach (var field in NotaCreditoConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validar los campos del documento sustento
        foreach (var field in NotaCreditoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required for Nota de Crédito.");
            }
        }

        // Validar que codDocModificado sea "01" (Factura)
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

        // Validar el formato de numDocModificado: 001-001-000000001
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

        // Validar que fechaEmisionDocSustento sea parseable como dd/MM/yyyy
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

        // Validar las tarifas de IVA
        foreach (var item in document.Items)
        {
            if (!ValidIvaRates.Contains(item.TaxRate))
            {
                errors.Add(
                    $"Item '{item.Description}' has invalid IVA rate {item.TaxRate}%. " +
                    $"Valid rates for 2026: {string.Join(", ", ValidIvaRates.OrderBy(r => r).Select(r => $"{r}%"))}.");
            }
        }

        // Validar los campos obligatorios del comprador
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
    /// Autogenera los campos del sistema (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// en el IssuerInfo del documento, luego delega la construcción del XML al builder inyectado.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Genera el RIDE PDF para la Nota de Crédito delegando al RideGenerator compartido.
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
    private void PopulateSystemFields(Document document)
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
    }
}
