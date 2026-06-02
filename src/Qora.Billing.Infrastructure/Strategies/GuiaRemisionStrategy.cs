using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Estrategia para el tipo de documento Guía de Remisión (documento de transporte).
/// Orquesta la validación específica de la guía, la autogeneración de campos del sistema,
/// la generación del XML y la generación del RIDE PDF (stub para el MVP).
/// Sin validación de IVA — las guías de remisión transportan mercancías, no impuestos.
/// </summary>
public class GuiaRemisionStrategy : IDocumentTypeStrategy
{
    private readonly GuiaRemisionXmlBuilder _xmlBuilder;
    private readonly IRideGenerator _rideGenerator;
    private readonly SriConfiguration _sriConfiguration;

    public DocumentType DocumentType => DocumentType.GuiaRemision;

    public GuiaRemisionStrategy(
        GuiaRemisionXmlBuilder xmlBuilder,
        IRideGenerator rideGenerator,
        IOptions<SriConfiguration> sriOptions)
    {
        _xmlBuilder = xmlBuilder ?? throw new ArgumentNullException(nameof(xmlBuilder));
        _rideGenerator = rideGenerator ?? throw new ArgumentNullException(nameof(rideGenerator));
        _sriConfiguration = sriOptions?.Value ?? throw new ArgumentNullException(nameof(sriOptions));
    }

    /// <summary>
    /// Valida las reglas de negocio específicas de la guía de remisión.
    /// Verifica los campos del emisor, transportista y destinatario usando GuiaRemisionConstants.
    /// Sin validación de tarifa de IVA — las guías de remisión no llevan información tributaria.
    /// Devuelve una lista de mensajes de error de validación (vacía si es válido).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Debe ser una GuiaRemision
        if (document.DocumentType != DocumentType.GuiaRemision)
        {
            errors.Add($"Expected document type GuiaRemision, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // ── Validación del destinatario ──────────────────────────────────────────
        if (document.Destinatarios.Count == 0 && document.BuyerInfo.Count == 0)
        {
            // No hay ni entidades del nuevo estilo ni el BuyerInfo legado
            errors.Add("GuiaRemision requires at least one destinatario.");
        }
        else if (document.Destinatarios.Count == 0)
        {
            // Ruta legada: debe tener al menos un ítem y campos de destinatario válidos en BuyerInfo
            if (document.Items.Count == 0)
            {
                errors.Add("Guía de Remisión must have at least one line item.");
            }

            foreach (var field in GuiaRemisionConstants.RequiredDestinatarioFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"Destinatario field '{field}' is required in BuyerInfo.");
                }
            }
        }
        else
        {
            // Ruta multi-destinatario: validar cada entidad Destinatario
            for (var i = 0; i < document.Destinatarios.Count; i++)
            {
                var dest = document.Destinatarios.ElementAt(i);
                var prefix = $"Destinatario[{i}]";

                if (string.IsNullOrWhiteSpace(dest.IdentificacionDestinatario))
                    errors.Add($"{prefix}.IdentificacionDestinatario is required.");

                if (string.IsNullOrWhiteSpace(dest.RazonSocialDestinatario))
                    errors.Add($"{prefix}.RazonSocialDestinatario is required.");

                if (string.IsNullOrWhiteSpace(dest.DirDestinatario))
                    errors.Add($"{prefix}.DirDestinatario is required.");

                if (dest.Items.Count == 0)
                    errors.Add($"{prefix} must have at least one detalle item.");

                foreach (var item in dest.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.DescripcionDetalle))
                        errors.Add($"{prefix}: DestinatarioItem.DescripcionDetalle is required.");

                    if (item.CantidadDetalle <= 0)
                        errors.Add($"{prefix}: DestinatarioItem.CantidadDetalle must be greater than zero.");
                }
            }

            // Los campos compartidos del documento de sustento (a nivel de documento) están en BuyerInfo
            foreach (var field in GuiaRemisionConstants.RequiredSustentoDocFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"Sustento doc field '{field}' is required in BuyerInfo.");
                }
            }
        }

        // ── Validación del emisor ────────────────────────────────────────────────

        // Validar los campos obligatorios del emisor provistos por el llamador
        foreach (var field in GuiaRemisionConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validar los campos del transportista (almacenados en IssuerInfo)
        foreach (var field in GuiaRemisionConstants.RequiredTransporterFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Transporter field '{field}' is required in IssuerInfo.");
            }
        }

        // Validar rucTransportista: debe tener exactamente 13 dígitos numéricos
        if (document.IssuerInfo.TryGetValue("rucTransportista", out var rucTransportista)
            && !string.IsNullOrWhiteSpace(rucTransportista))
        {
            if (rucTransportista.Length != 13 || !rucTransportista.All(char.IsDigit))
            {
                errors.Add("Transporter field 'rucTransportista' must be exactly 13 numeric digits.");
            }
        }

        // Validar los campos de fechas de transporte
        if (document.IssuerInfo.TryGetValue("fechaInicioTransporte", out var fechaInicio)
            && !string.IsNullOrWhiteSpace(fechaInicio))
        {
            if (!DateTime.TryParseExact(fechaInicio, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _))
            {
                errors.Add("Transporter field 'fechaInicioTransporte' must be in dd/MM/yyyy format.");
            }
        }

        if (document.IssuerInfo.TryGetValue("fechaFinTransporte", out var fechaFin)
            && !string.IsNullOrWhiteSpace(fechaFin))
        {
            if (!DateTime.TryParseExact(fechaFin, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _))
            {
                errors.Add("Transporter field 'fechaFinTransporte' must be in dd/MM/yyyy format.");
            }
        }

        // Validar fechaFinTransporte >= fechaInicioTransporte (solo si ambas son fechas válidas)
        if (document.IssuerInfo.TryGetValue("fechaInicioTransporte", out var rawInicio)
            && document.IssuerInfo.TryGetValue("fechaFinTransporte", out var rawFin)
            && !string.IsNullOrWhiteSpace(rawInicio)
            && !string.IsNullOrWhiteSpace(rawFin)
            && DateTime.TryParseExact(rawInicio, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsedInicio)
            && DateTime.TryParseExact(rawFin, "dd/MM/yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsedFin))
        {
            if (parsedFin < parsedInicio)
            {
                errors.Add("'fechaFinTransporte' must be greater than or equal to 'fechaInicioTransporte'.");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    /// <summary>
    /// Autogenera los campos del sistema (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// en el IssuerInfo del documento, luego delega la construcción del XML al GuiaRemisionXmlBuilder inyectado.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Genera el RIDE PDF para la Guía de Remisión delegando al RideGenerator compartido.
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
