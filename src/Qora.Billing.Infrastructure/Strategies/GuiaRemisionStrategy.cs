using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure.Strategies;

/// <summary>
/// Strategy for Guía de Remisión (transport document) document type.
/// Orchestrates guía-specific validation, system field auto-generation,
/// XML generation, and RIDE PDF generation (stub for MVP).
/// No IVA validation — guías de remisión carry goods, not taxes.
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
    /// Validates guía de remisión-specific business rules.
    /// Checks issuer, transporter, and destinatario fields using GuiaRemisionConstants.
    /// No IVA rate validation — guías de remisión do not carry tax information.
    /// Returns a list of validation error messages (empty if valid).
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateDocumentAsync(Document document,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Must be a GuiaRemision
        if (document.DocumentType != DocumentType.GuiaRemision)
        {
            errors.Add($"Expected document type GuiaRemision, got {document.DocumentType}.");
            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        // ── Destinatario validation ──────────────────────────────────────────
        if (document.Destinatarios.Count == 0 && document.BuyerInfo.Count == 0)
        {
            // Neither new-style entities nor legacy BuyerInfo present
            errors.Add("GuiaRemision requires at least one destinatario.");
        }
        else if (document.Destinatarios.Count == 0)
        {
            // Legacy path: must have at least one item and valid BuyerInfo destinatario fields
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
            // Multi-destinatario path: validate each Destinatario entity
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

            // Shared sustento doc fields (document-level) are in BuyerInfo
            foreach (var field in GuiaRemisionConstants.RequiredSustentoDocFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"Sustento doc field '{field}' is required in BuyerInfo.");
                }
            }
        }

        // ── Issuer validation ────────────────────────────────────────────────

        // Validate caller-provided issuer required fields
        foreach (var field in GuiaRemisionConstants.RequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Issuer field '{field}' is required.");
            }
        }

        // Validate transporter fields (stored in IssuerInfo)
        foreach (var field in GuiaRemisionConstants.RequiredTransporterFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Transporter field '{field}' is required in IssuerInfo.");
            }
        }

        // Validate rucTransportista: must be exactly 13 numeric digits
        if (document.IssuerInfo.TryGetValue("rucTransportista", out var rucTransportista)
            && !string.IsNullOrWhiteSpace(rucTransportista))
        {
            if (rucTransportista.Length != 13 || !rucTransportista.All(char.IsDigit))
            {
                errors.Add("Transporter field 'rucTransportista' must be exactly 13 numeric digits.");
            }
        }

        // Validate transport date fields
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

        // Validate fechaFinTransporte >= fechaInicioTransporte (only if both are valid dates)
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
    /// Auto-generates system fields (ambiente, tipoEmision, claveAcceso, fechaEmision)
    /// into the document's IssuerInfo, then delegates XML building to the injected GuiaRemisionXmlBuilder.
    /// </summary>
    public Task<string> BuildXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        PopulateSystemFields(document);
        return _xmlBuilder.GenerateXmlAsync(document, cancellationToken);
    }

    /// <summary>
    /// Generates RIDE PDF for Guía de Remisión by delegating to the shared RideGenerator.
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
