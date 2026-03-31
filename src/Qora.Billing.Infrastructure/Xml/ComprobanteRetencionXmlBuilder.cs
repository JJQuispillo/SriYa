using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Generates unsigned XML for Comprobante de Retención documents following
/// SRI Ecuador schema v2.0.0. Uses System.Xml.Linq for XML construction.
///
/// DocumentItem field mapping for retenciones:
///   item.TaxCode                    → impuesto/codigo              (SRI tax type: "1"=Renta, "2"=IVA, "6"=ISD)
///   item.TaxPercentageCode          → impuesto/codigoPorcentaje    (SRI retention % code)
///   item.TaxRate                    → impuesto/tarifa              (retention percentage, e.g. 1.75)
///   item.Quantity * item.UnitPrice  → impuesto/baseImponible       (gross base, pre-discount per SRI)
///   baseImponible * tarifa / 100    → impuesto/valorRetenido       (computed; no TaxAmount field exists)
///   item.SustentoDocumentType       → impuesto/codDocSustento      (falls back to item.MainCode when null)
///   item.SustentoDocumentNumber     → impuesto/numDocSustento
///   item.SustentoDocumentIssueDate  → impuesto/fechaEmisionDocSustento (formatted dd/MM/yyyy)
///   item.SustentoDocumentAuthNumber → impuesto/numAutDocSustento
/// </summary>
public class ComprobanteRetencionXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.ComprobanteRetencion)
            throw new DocumentValidationException(
                $"ComprobanteRetencionXmlBuilder only supports ComprobanteRetencion documents, got {document.DocumentType}.");

        var xml = BuildComprobanteRetencionXml(document);
        return Task.FromResult(xml);
    }

    private string BuildComprobanteRetencionXml(Document document)
    {
        ValidateRequiredFields(document);

        var root = new XElement("comprobanteRetencion",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "2.0.0"),
            BuildInfoTributaria(document),
            BuildInfoCompRetencion(document),
            BuildImpuestos(document),
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
                root);
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
            new XElement("codDoc", ((int)DocumentType.ComprobanteRetencion).ToString("D2")),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoCompRetencion(Document document)
    {
        var issuer = document.IssuerInfo;
        var buyer = document.BuyerInfo;

        // dirEstablecimiento: prefer specific field, fall back to dirMatriz
        var dirEstablecimiento = issuer.GetValueOrDefault("dirEstablecimiento")
                                 ?? issuer.GetValueOrDefault("dirMatriz");

        var infoCompRetencion = new XElement("infoCompRetencion",
            new XElement("fechaEmision", GetRequiredValue(issuer, "fechaEmision")),
            OptionalElement("dirEstablecimiento", dirEstablecimiento),
            OptionalElement("obligadoContabilidad", issuer.GetValueOrDefault("obligadoContabilidad")),
            OptionalElement("contribuyenteEspecial", issuer.GetValueOrDefault("contribuyenteEspecial")),
            new XElement("tipoIdentificacionSujetoRetenido", GetRequiredValue(buyer, "tipoIdentificacion")),
            new XElement("razonSocialSujetoRetenido", GetRequiredValue(buyer, "razonSocial")),
            new XElement("identificacionSujetoRetenido", GetRequiredValue(buyer, "identificacion")),
            new XElement("periodoFiscal", GetRequiredValue(issuer, "periodoFiscal")));

        return infoCompRetencion;
    }

    private static XElement BuildImpuestos(Document document)
    {
        var impuestos = new XElement("impuestos");

        foreach (var item in document.Items)
        {
            // baseImponible = Quantity * UnitPrice (gross, pre-discount, per SRI retention schema)
            var baseImponible = item.Quantity * item.UnitPrice;

            // valorRetenido computed from base and tarifa (DocumentItem has no TaxAmount field)
            var valorRetenido = baseImponible * item.TaxRate / 100m;

            // codDocSustento: typed domain property, falls back to MainCode when null
            var codDocSustento = !string.IsNullOrWhiteSpace(item.SustentoDocumentType)
                ? item.SustentoDocumentType
                : item.MainCode;

            // fechaEmisionDocSustento formatted as dd/MM/yyyy per SRI schema
            var fechaEmision = item.SustentoDocumentIssueDate.HasValue
                ? item.SustentoDocumentIssueDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : string.Empty;

            var impuesto = new XElement("impuesto",
                new XElement("codigo", item.TaxCode),
                new XElement("codigoPorcentaje", item.TaxPercentageCode),
                new XElement("tarifa", item.TaxRate.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("baseImponible", baseImponible.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("valorRetenido", valorRetenido.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("codDocSustento", codDocSustento),
                new XElement("numDocSustento", item.SustentoDocumentNumber ?? string.Empty),
                new XElement("fechaEmisionDocSustento", fechaEmision),
                new XElement("numAutDocSustento", item.SustentoDocumentAuthNumber ?? string.Empty));

            impuestos.Add(impuesto);
        }

        return impuestos;
    }

    private static XElement? BuildInfoAdicional(Document document)
    {
        var buyer = document.BuyerInfo;
        var additionalFields = new List<(string Name, string? Value)>
        {
            ("email", buyer.GetValueOrDefault("email")),
            ("direccion", buyer.GetValueOrDefault("direccion")),
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
    /// Validates that all required fields from ComprobanteRetencionConstants are present
    /// in the document before XML generation proceeds.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in ComprobanteRetencionConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in ComprobanteRetencionConstants.RequiredBuyerFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"buyer.{field}");
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
            throw new DocumentValidationException($"Required field '{key}' is missing or empty.");

        return value;
    }

    private static XElement? OptionalElement(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
    }
}
