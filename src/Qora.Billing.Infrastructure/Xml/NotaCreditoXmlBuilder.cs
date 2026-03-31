using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Generates unsigned XML for Nota de Crédito documents following SRI XSD v1.0.0 schema.
/// Uses System.Xml.Linq for cleaner XML construction.
/// </summary>
public class NotaCreditoXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.NotaCredito)
            throw new DocumentValidationException($"NotaCreditoXmlBuilder only supports NotaCredito documents, got {document.DocumentType}.");

        var xml = BuildNotaCreditoXml(document);
        return Task.FromResult(xml);
    }

    private string BuildNotaCreditoXml(Document document)
    {
        ValidateRequiredFields(document);

        var notaCredito = new XElement("notaCredito",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "1.0.0"),
            BuildInfoTributaria(document),
            BuildInfoNotaCredito(document),
            BuildDetalles(document),
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
                notaCredito);
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
            new XElement("codDoc", "04"),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoNotaCredito(Document document)
    {
        var issuer = document.IssuerInfo;
        var buyer = document.BuyerInfo;

        var totalSinImpuestos = document.Items.Sum(i => i.Subtotal);
        var totalTax = document.Items.Sum(i => i.Subtotal * i.TaxRate / 100m);
        var valorModificacion = totalSinImpuestos + totalTax;

        // dirEstablecimiento: use specific address if provided, fall back to dirMatriz
        var dirEstablecimiento = issuer.GetValueOrDefault("dirEstablecimiento")
            ?? GetRequiredValue(issuer, "dirMatriz");

        var infoNotaCredito = new XElement("infoNotaCredito",
            new XElement("fechaEmision", GetRequiredValue(issuer, "fechaEmision")),
            new XElement("dirEstablecimiento", dirEstablecimiento),
            new XElement("tipoIdentificacionComprador", GetRequiredValue(buyer, "tipoIdentificacion")),
            new XElement("razonSocialComprador", GetRequiredValue(buyer, "razonSocial")),
            new XElement("identificacionComprador", GetRequiredValue(buyer, "identificacion")),
            OptionalElement("contribuyenteEspecial", issuer.GetValueOrDefault("contribuyenteEspecial")),
            OptionalElement("obligadoContabilidad", issuer.GetValueOrDefault("obligadoContabilidad")),
            new XElement("codDocModificado", GetRequiredValue(issuer, "codDocModificado")),
            new XElement("numDocModificado", GetRequiredValue(issuer, "numDocModificado")),
            new XElement("fechaEmisionDocSustento", GetRequiredValue(issuer, "fechaEmisionDocSustento")),
            new XElement("totalSinImpuestos", totalSinImpuestos.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("valorModificacion", valorModificacion.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("moneda", "DOLAR"),
            BuildTotalConImpuestos(document),
            new XElement("motivo", GetRequiredValue(issuer, "razonModificacion")));

        return infoNotaCredito;
    }

    private static XElement BuildTotalConImpuestos(Document document)
    {
        var taxGroups = document.Items
            .GroupBy(i => new { i.TaxCode, i.TaxPercentageCode })
            .Select(g => new
            {
                g.Key.TaxCode,
                g.Key.TaxPercentageCode,
                BaseImponible = g.Sum(i => i.Subtotal),
                Valor = g.Sum(i => i.Subtotal * i.TaxRate / 100m)
            });

        var totalConImpuestos = new XElement("totalConImpuestos");

        foreach (var group in taxGroups)
        {
            totalConImpuestos.Add(new XElement("totalImpuesto",
                new XElement("codigo", group.TaxCode),
                new XElement("codigoPorcentaje", group.TaxPercentageCode),
                new XElement("baseImponible", group.BaseImponible.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("valor", group.Valor.ToString("F2", CultureInfo.InvariantCulture))));
        }

        return totalConImpuestos;
    }

    private static XElement BuildDetalles(Document document)
    {
        var detalles = new XElement("detalles");

        foreach (var item in document.Items)
        {
            var precioTotalSinImpuesto = item.Subtotal;
            var taxValue = precioTotalSinImpuesto * item.TaxRate / 100m;

            var detalle = new XElement("detalle",
                new XElement("codigoInterno", item.MainCode),
                new XElement("descripcion", item.Description),
                new XElement("cantidad", item.Quantity.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement("precioUnitario", item.UnitPrice.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement("descuento", item.Discount.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("precioTotalSinImpuesto", precioTotalSinImpuesto.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("impuestos",
                    new XElement("impuesto",
                        new XElement("codigo", item.TaxCode),
                        new XElement("codigoPorcentaje", item.TaxPercentageCode),
                        new XElement("tarifa", item.TaxRate.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement("baseImponible", precioTotalSinImpuesto.ToString("F2", CultureInfo.InvariantCulture)),
                        new XElement("valor", taxValue.ToString("F2", CultureInfo.InvariantCulture)))));

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
    /// Validates that all required fields from NotaCreditoConstants are present
    /// in the document before XML generation proceeds.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in NotaCreditoConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in NotaCreditoConstants.RequiredDocSustentoFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in NotaCreditoConstants.RequiredBuyerFields)
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
