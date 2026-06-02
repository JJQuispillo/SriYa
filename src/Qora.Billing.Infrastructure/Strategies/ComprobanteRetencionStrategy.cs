using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Estrategia para el tipo de documento Comprobante de Retención.
/// Orquesta la validación específica de la retención, la autogeneración de campos del sistema,
/// la generación del XML y la generación del RIDE PDF.
///
/// Uso de los campos de DocumentItem:
///   item.TaxCode              → validado contra ValidTaxCodes ("1"=Renta, "2"=IVA, "6"=ISD)
///   item.TaxPercentageCode    → código de porcentaje de retención del SRI (se pasa al XML)
///   item.TaxRate              → porcentaje de retención, debe ser > 0
///   item.SustentoDocumentType → código del SRI para el tipo de documento de sustento (requerido)
///   item.SustentoDocumentNumber → número del documento de sustento (requerido)
/// </summary>
public class ComprobanteRetencionStrategy : IDocumentTypeStrategy
{
    private readonly ComprobanteRetencionXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Patrón regex para periodoFiscal: formato MM/YYYY.
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
    /// Valida las reglas de negocio específicas del comprobante de retención.
    /// Devuelve una lista de mensajes de error de validación (vacía si es válido).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Debe ser un ComprobanteRetencion
        if (document.DocumentType != DocumentType.ComprobanteRetencion)
        {
            errors.Add($"Expected document type ComprobanteRetencion, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Debe tener al menos una línea de retención
        if (document.Items.Count == 0)
        {
            errors.Add("Comprobante de Retención must have at least one retention line (impuesto).");
        }

        // Validar los campos obligatorios del emisor provistos por el llamador
        foreach (var field in ComprobanteRetencionConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validar el formato de periodoFiscal: MM/YYYY
        if (document.IssuerInfo.TryGetValue("periodoFiscal", out var periodoFiscal)
            && !string.IsNullOrWhiteSpace(periodoFiscal))
        {
            if (!PeriodoFiscalRegex.IsMatch(periodoFiscal))
            {
                errors.Add(
                    $"Issuer field 'periodoFiscal' must be in MM/YYYY format (e.g., '01/2026'), got '{periodoFiscal}'.");
            }
        }

        // Validar los campos obligatorios del comprador (sujeto retenido)
        foreach (var field in ComprobanteRetencionConstants.RequiredBuyerFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Buyer field '{field}' is required.");
            }
        }

        // Validar cada línea de retención
        foreach (var item in document.Items)
        {
            // TaxCode (item.TaxCode) debe ser un tipo de impuesto de retención válido del SRI
            if (!ComprobanteRetencionConstants.ValidTaxCodes.Contains(item.TaxCode))
            {
                errors.Add(
                    $"Item '{item.Description}' has invalid tax type code '{item.TaxCode}'. " +
                    $"Valid codes: {string.Join(", ", ComprobanteRetencionConstants.ValidTaxCodes)} " +
                    "(1=Renta, 2=IVA, 6=ISD).");
            }

            // TaxRate (porcentaje de retención) debe ser positivo
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
    /// Autogenera los campos del sistema (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// en el IssuerInfo del documento, luego delega la construcción del XML al ComprobanteRetencionXmlBuilder inyectado.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        // Validar que cada línea de retención tenga los campos de sustento requeridos
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
    /// Genera el RIDE PDF para el Comprobante de Retención delegando al RideGenerator compartido.
    /// </summary>
    public Task<byte[]> BuildRidePdfAsync(Document document, CancellationToken cancellationToken = default)
    {
        return _rideGenerator.GeneratePdfAsync(document, cancellationToken);
    }

    /// <summary>
    /// Rellena los campos generados por el sistema en el diccionario IssuerInfo del documento
    /// antes de la generación del XML. Usa la configuración del SRI para el ambiente y el tipo de emisión,
    /// y AccessKeyGenerator para la clave de acceso de 49 dígitos.
    /// Misma lógica que FacturaStrategy.PopulateSystemFields.
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
