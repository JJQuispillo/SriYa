using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Genera el XML sin firmar para documentos Guía de Remisión siguiendo el esquema XSD v1.1.0 del SRI.
/// Usa System.Xml.Linq para una construcción de XML más limpia.
/// Sin IVA ni impuestos — las guías de remisión son documentos de transporte, no comprobantes fiscales.
/// MVP: un solo destinatario por documento.
/// </summary>
public class GuiaRemisionXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.GuiaRemision)
            throw new DocumentValidationException(
                $"GuiaRemisionXmlBuilder only supports GuiaRemision documents, got {document.DocumentType}.");

        var xml = BuildGuiaRemisionXml(document);
        return Task.FromResult(xml);
    }

    private string BuildGuiaRemisionXml(Document document)
    {
        ValidateRequiredFields(document);

        var guiaRemision = new XElement("guiaRemision",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "1.1.0"),
            BuildInfoTributaria(document),
            BuildInfoGuiaRemision(document),
            BuildDestinatarios(document),
            BuildInfoAdicional(document));

        // UTF-8 without BOM
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
        {
            var xDoc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                guiaRemision);
            xDoc.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XElement BuildInfoTributaria(Document document)
    {
        var issuer = document.IssuerInfo;

        return new XElement("infoTributaria",
            new XElement("ambiente", GetRequiredValue(issuer, "ambiente")),
            new XElement("tipoEmision", GetRequiredValue(issuer, "tipoEmision")),
            new XElement("razonSocial", GetRequiredValue(issuer, "razonSocial")),
            OptionalElement("nombreComercial", issuer.GetValueOrDefault("nombreComercial")),
            new XElement("ruc", GetRequiredValue(issuer, "ruc")),
            new XElement("claveAcceso", GetRequiredValue(issuer, "claveAcceso")),
            new XElement("codDoc", ((int)DocumentType.GuiaRemision).ToString("D2")),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoGuiaRemision(Document document)
    {
        var issuer = document.IssuerInfo;

        // dirEstablecimiento: prefer explicit value, fall back to dirMatriz
        var dirEstablecimiento = issuer.GetValueOrDefault("dirEstablecimiento")
            ?? issuer.GetValueOrDefault("dirMatriz")
            ?? string.Empty;

        var infoGuiaRemision = new XElement("infoGuiaRemision",
            new XElement("dirEstablecimiento", dirEstablecimiento),
            new XElement("dirPartida", GetRequiredValue(issuer, "dirMatriz")),
            new XElement("razonSocialTransportista", GetRequiredValue(issuer, "razonSocialTransportista")),
            new XElement("tipoIdentificacionTransportista", "04"), // 04 = RUC, always for transportista
            new XElement("rucTransportista", GetRequiredValue(issuer, "rucTransportista")),
            new XElement("obligadoContabilidad", GetRequiredValue(issuer, "obligadoContabilidad")),
            new XElement("fechaIniTransporte", GetRequiredValue(issuer, "fechaInicioTransporte")),
            new XElement("fechaFinTransporte", GetRequiredValue(issuer, "fechaFinTransporte")),
            new XElement("placa", GetRequiredValue(issuer, "placa")),
            OptionalElement("rise", issuer.GetValueOrDefault("rise")),
            OptionalElement("contribuyenteEspecial", issuer.GetValueOrDefault("contribuyenteEspecial")));

        return infoGuiaRemision;
    }

    /// <summary>
    /// Builds the &lt;destinatarios&gt; block.
    /// If <see cref="Document.Destinatarios"/> has entries, iterates them (multi-destinatario path).
    /// Otherwise falls back to legacy single-destinatario from BuyerInfo + Document.Items.
    /// </summary>
    private static XElement BuildDestinatarios(Document document)
    {
        var destinatariosElement = new XElement(GuiaRemisionConstants.DestinatariosTag);

        if (document.Destinatarios.Count > 0)
        {
            // ── Multi-destinatario path ──────────────────────────────────────
            // Sustento doc fields are document-level; read from BuyerInfo for now
            // (the Destinatario entity does not yet carry them; they are shared across all destinatarios)
            var buyer = document.BuyerInfo;
            var codDocSustento = buyer.GetValueOrDefault("codDocSustento") ?? string.Empty;
            var numDocSustento = buyer.GetValueOrDefault("numDocSustento") ?? string.Empty;
            var numAutDocSustento = buyer.GetValueOrDefault("numAutDocSustento");
            var fechaEmisionDocSustento = buyer.GetValueOrDefault("fechaEmisionDocSustento");

            foreach (var dest in document.Destinatarios)
            {
                var destinatarioElement = new XElement(GuiaRemisionConstants.DestinatarioTag,
                    new XElement(GuiaRemisionConstants.IdentificacionDestinatarioTag, dest.IdentificacionDestinatario),
                    new XElement(GuiaRemisionConstants.RazonSocialDestinatarioTag, dest.RazonSocialDestinatario),
                    new XElement(GuiaRemisionConstants.DirDestinatarioTag, dest.DirDestinatario),
                    new XElement(GuiaRemisionConstants.MotivoTrasladoTag, dest.MotivoTraslado),
                    OptionalElement(GuiaRemisionConstants.RutaTag, dest.RutaEntrega),
                    OptionalElement(GuiaRemisionConstants.DocAduaneroUnicoTag, dest.DocAduaneroUnico),
                    OptionalElement(GuiaRemisionConstants.CodEstabDestinoTag, dest.CodEstabDestino),
                    new XElement(GuiaRemisionConstants.CodDocSustentoTag, codDocSustento),
                    new XElement(GuiaRemisionConstants.NumDocSustentoTag, numDocSustento),
                    OptionalElement(GuiaRemisionConstants.NumAutDocSustentoTag, numAutDocSustento),
                    OptionalElement(GuiaRemisionConstants.FechaEmisionDocSustentoTag, fechaEmisionDocSustento),
                    BuildDetallesFromDestinatario(dest));

                destinatariosElement.Add(destinatarioElement);
            }
        }
        else
        {
            // ── Legacy single-destinatario fallback (BuyerInfo + Document.Items) ──
            var buyer = document.BuyerInfo;

            var destinatarioElement = new XElement(GuiaRemisionConstants.DestinatarioTag,
                new XElement(GuiaRemisionConstants.IdentificacionDestinatarioTag,
                    GetRequiredValue(buyer, "identificacionDestinatario")),
                new XElement(GuiaRemisionConstants.RazonSocialDestinatarioTag,
                    GetRequiredValue(buyer, "razonSocialDestinatario")),
                new XElement(GuiaRemisionConstants.DirDestinatarioTag,
                    GetRequiredValue(buyer, "dirDestinatario")),
                new XElement("motivoTraslado", GetRequiredValue(buyer, "motivoTraslado")),
                OptionalElement(GuiaRemisionConstants.DocAduaneroUnicoTag, buyer.GetValueOrDefault("docAduaneroUnico")),
                OptionalElement(GuiaRemisionConstants.CodEstabDestinoTag, buyer.GetValueOrDefault("codEstabDestino")),
                OptionalElement(GuiaRemisionConstants.RutaTag, buyer.GetValueOrDefault("ruta")),
                new XElement(GuiaRemisionConstants.CodDocSustentoTag,
                    GetRequiredValue(buyer, "codDocSustento")),
                new XElement(GuiaRemisionConstants.NumDocSustentoTag,
                    GetRequiredValue(buyer, "numDocSustento")),
                new XElement(GuiaRemisionConstants.NumAutDocSustentoTag,
                    GetRequiredValue(buyer, "numAutDocSustento")),
                new XElement(GuiaRemisionConstants.FechaEmisionDocSustentoTag,
                    GetRequiredValue(buyer, "fechaEmisionDocSustento")),
                BuildDetallesFromItems(document));

            destinatariosElement.Add(destinatarioElement);
        }

        return destinatariosElement;
    }

    /// <summary>
    /// Builds &lt;detalles&gt; from a <see cref="Destinatario"/>'s own item list (multi-destinatario path).
    /// </summary>
    private static XElement BuildDetallesFromDestinatario(Domain.Entities.Destinatario destinatario)
    {
        var detalles = new XElement(GuiaRemisionConstants.DetallesTag);

        foreach (var item in destinatario.Items)
        {
            var detalle = new XElement(GuiaRemisionConstants.DetalleTag,
                new XElement(GuiaRemisionConstants.CodigoInternoTag, item.CodigoInterno),
                new XElement(GuiaRemisionConstants.DescripcionDetalleTag, item.DescripcionDetalle),
                new XElement(GuiaRemisionConstants.CantidadDetalleTag,
                    item.CantidadDetalle.ToString("F6", CultureInfo.InvariantCulture)));

            detalles.Add(detalle);
        }

        return detalles;
    }

    /// <summary>
    /// Builds &lt;detalles&gt; from Document.Items (legacy single-destinatario fallback).
    /// </summary>
    private static XElement BuildDetallesFromItems(Document document)
    {
        var detalles = new XElement(GuiaRemisionConstants.DetallesTag);

        foreach (var item in document.Items)
        {
            var detalle = new XElement(GuiaRemisionConstants.DetalleTag,
                new XElement(GuiaRemisionConstants.CodigoInternoTag, item.MainCode),
                new XElement(GuiaRemisionConstants.DescripcionDetalleTag, item.Description),
                new XElement(GuiaRemisionConstants.CantidadDetalleTag,
                    item.Quantity.ToString("F6", CultureInfo.InvariantCulture)));

            detalles.Add(detalle);
        }

        return detalles;
    }

    private static XElement? BuildInfoAdicional(Document document)
    {
        var buyer = document.BuyerInfo;
        var additionalFields = new List<(string Name, string? Value)>
        {
            ("email", buyer.GetValueOrDefault("email")),
            ("telefono", buyer.GetValueOrDefault("telefono"))
        };

        var validFields = additionalFields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (validFields.Count == 0)
            return null;

        var infoAdicional = new XElement("infoAdicional");
        foreach (var (name, value) in validFields)
        {
            infoAdicional.Add(new XElement("campoAdicional",
                new XAttribute("nombre", name),
                value!));
        }

        return infoAdicional;
    }

    /// <summary>
    /// Validates that all required fields from GuiaRemisionConstants are present
    /// in the document before XML generation proceeds.
    /// When <see cref="Document.Destinatarios"/> has entries, skips BuyerInfo destinatario field
    /// validation (per-entity fields are validated by GuiaRemisionStrategy instead).
    /// Still validates BuyerInfo sustento doc fields (codDocSustento, numDocSustento) which
    /// are document-level and shared across destinatarios.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in GuiaRemisionConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in GuiaRemisionConstants.RequiredTransporterFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        if (document.Destinatarios.Count == 0)
        {
            // Legacy path: validate full destinatario fields in BuyerInfo
            foreach (var field in GuiaRemisionConstants.RequiredDestinatarioFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    missingFields.Add($"buyer.{field}");
                }
            }
        }
        else
        {
            // Multi-destinatario path: only validate shared sustento doc fields in BuyerInfo
            foreach (var field in GuiaRemisionConstants.RequiredSustentoDocFields)
            {
                if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    missingFields.Add($"buyer.{field}");
                }
            }
        }

        if (missingFields.Count > 0)
        {
            throw new DocumentValidationException(
                $"Required fields missing for XML generation: {string.Join(", ", missingFields)}.");
        }
    }

    private static string GetRequiredValue(Dictionary<string, string> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new DocumentValidationException($"El campo requerido '{key}' está vacío o ausente.");

        return value;
    }

    private static XElement? OptionalElement(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
    }
}
