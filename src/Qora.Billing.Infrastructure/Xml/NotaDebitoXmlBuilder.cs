using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Genera el XML sin firmar para documentos Nota de Débito siguiendo el esquema XSD v1.0.0 del SRI.
/// Diferencia clave respecto a la Factura: los cargos se expresan como <motivos> (no <detalles>).
/// Cada DocumentItem se mapea a un <motivo> con razon y valor.
/// Usa System.Xml.Linq para una construcción de XML más limpia.
/// </summary>
public class NotaDebitoXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.NotaDebito)
            throw new DocumentValidationException($"NotaDebitoXmlBuilder solo soporta documentos de tipo NotaDebito, se recibió {document.DocumentType}.");

        var xml = BuildNotaDebitoXml(document);
        return Task.FromResult(xml);
    }

    private string BuildNotaDebitoXml(Document document)
    {
        ValidateRequiredFields(document);

        var notaDebito = new XElement("notaDebito",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "1.0.0"),
            BuildInfoTributaria(document),
            BuildInfoNotaDebito(document),
            BuildMotivos(document),
            BuildInfoAdicional(document));

        // UTF-8 sin BOM
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
                notaDebito);
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
            new XElement("codDoc", ((int)DocumentType.NotaDebito).ToString("D2")),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoNotaDebito(Document document)
    {
        var issuer = document.IssuerInfo;
        var buyer = document.BuyerInfo;

        // totalSinImpuestos: sum of UnitPrice * Quantity for all motivos (no discount applied for NdD)
        var totalSinImpuestos = document.Items.Sum(i => i.UnitPrice * i.Quantity);

        var infoNotaDebito = new XElement("infoNotaDebito",
            new XElement("fechaEmision", GetRequiredValue(issuer, "fechaEmision")),
            new XElement("dirEstablecimiento", issuer.GetValueOrDefault("dirEstablecimiento") ?? GetRequiredValue(issuer, "dirMatriz")),
            new XElement("tipoIdentificacionComprador", GetRequiredValue(buyer, "tipoIdentificacion")),
            new XElement("razonSocialComprador", GetRequiredValue(buyer, "razonSocial")),
            new XElement("identificacionComprador", GetRequiredValue(buyer, "identificacion")),
            OptionalElement("contribuyenteEspecial", issuer.GetValueOrDefault("contribuyenteEspecial")),
            new XElement("obligadoContabilidad", issuer.GetValueOrDefault("obligadoContabilidad") ?? "NO"),
            new XElement("codDocSustento", GetRequiredValue(issuer, "codDocSustento")),
            new XElement("numDocSustento", GetRequiredValue(issuer, "numDocSustento")),
            new XElement("fechaEmisionDocSustento", GetRequiredValue(issuer, "fechaEmisionDocSustento")),
            new XElement("totalSinImpuestos", totalSinImpuestos.ToString("F2", CultureInfo.InvariantCulture)),
            BuildImpuestos(document));

        return infoNotaDebito;
    }

    private static XElement BuildImpuestos(Document document)
    {
        // Agrupar los ítems por TaxCode + TaxPercentageCode, usando UnitPrice * Quantity como base (sin descuento para NdD)
        var taxGroups = document.Items
            .GroupBy(i => new { i.TaxCode, i.TaxPercentageCode, i.TaxRate })
            .Select(g => new
            {
                g.Key.TaxCode,
                g.Key.TaxPercentageCode,
                g.Key.TaxRate,
                BaseImponible = g.Sum(i => i.UnitPrice * i.Quantity),
                Valor = g.Sum(i => i.UnitPrice * i.Quantity * i.TaxRate / 100m)
            });

        var impuestos = new XElement("impuestos");

        foreach (var group in taxGroups)
        {
            impuestos.Add(new XElement("impuesto",
                new XElement("codigo", group.TaxCode),
                new XElement("codigoPorcentaje", group.TaxPercentageCode),
                new XElement("tarifa", group.TaxRate.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("baseImponible", group.BaseImponible.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("valor", group.Valor.ToString("F2", CultureInfo.InvariantCulture))));
        }

        return impuestos;
    }

    private static XElement BuildMotivos(Document document)
    {
        var motivos = new XElement("motivos");

        foreach (var item in document.Items)
        {
            var valor = item.UnitPrice * item.Quantity;

            motivos.Add(new XElement("motivo",
                new XElement("razon", item.Description),
                new XElement("valor", valor.ToString("F2", CultureInfo.InvariantCulture))));
        }

        return motivos;
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
    /// Validates that all required fields from NotaDebitoConstants are present
    /// in the document before XML generation proceeds.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in NotaDebitoConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in NotaDebitoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in NotaDebitoConstants.RequiredBuyerFields)
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
            throw new DocumentValidationException($"El campo requerido '{key}' está vacío o ausente.");

        return value;
    }

    private static XElement? OptionalElement(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
    }
}
