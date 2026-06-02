using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Estrategia para el tipo de documento Liquidación de Compra.
/// Orquesta la validación específica de la liquidación, la autogeneración de campos del sistema,
/// la generación del XML y la generación del RIDE PDF.
/// </summary>
public class LiquidacionCompraStrategy : IDocumentTypeStrategy
{
    private readonly LiquidacionCompraXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    /// <summary>
    /// Tarifas de IVA válidas para Ecuador (2026): 0%, 5%, 12%, 15%.
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
    /// Valida las reglas de negocio específicas de la liquidación de compra.
    /// Verifica TODOS los campos que requiere LiquidacionCompraXmlBuilder, usando LiquidacionCompraConstants
    /// para mantener la alineación entre la validación y la generación del XML.
    /// Devuelve una lista de mensajes de error de validación (vacía si es válido).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Debe ser una LiquidacionCompra
        if (document.DocumentType != DocumentType.LiquidacionCompra)
        {
            errors.Add($"Expected document type LiquidacionCompra, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // Debe tener al menos un ítem
        if (document.Items.Count == 0)
        {
            errors.Add("LiquidacionCompra must have at least one line item.");
        }

        // Validar los campos obligatorios del emisor provistos por el llamador
        foreach (var field in LiquidacionCompraConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validar los campos del proveedor en BuyerInfo
        foreach (var field in LiquidacionCompraConstants.RequiredProviderFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Provider field '{field}' is required.");
            }
        }

        // Validar el tipo de identificación del proveedor
        if (document.BuyerInfo.TryGetValue("tipoIdentificacionProveedor", out var tipoId)
            && !string.IsNullOrWhiteSpace(tipoId)
            && !LiquidacionCompraConstants.ValidProviderIdTypes.Contains(tipoId))
        {
            errors.Add(
                $"Provider identification type '{tipoId}' is not valid. " +
                $"Valid types: {string.Join(", ", LiquidacionCompraConstants.ValidProviderIdTypes.OrderBy(t => t))}.");
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

        // Validar los campos de retención: si codigoRetencion está presente, todos los campos de retención deben estar presentes
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
    /// Autogenera los campos del sistema (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// en el IssuerInfo del documento, luego delega la construcción del XML al builder inyectado.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Genera el RIDE PDF para la Liquidación de Compra delegando al RideGenerator compartido.
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
