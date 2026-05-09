using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Generates unsigned XML for Liquidación de Compra documents following SRI XSD v1.1.0 schema.
/// Uses System.Xml.Linq for cleaner XML construction.
/// </summary>
public class LiquidacionCompraXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.LiquidacionCompra)
            throw new DocumentValidationException(
                $"LiquidacionCompraXmlBuilder only supports LiquidacionCompra documents, got {document.DocumentType}.");

        var xml = BuildLiquidacionCompraXml(document);
        return Task.FromResult(xml);
    }

    private string BuildLiquidacionCompraXml(Document document)
    {
        ValidateRequiredFields(document);

        var liquidacionCompra = new XElement("liquidacionCompra",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "1.1.0"),
            BuildInfoTributaria(document),
            BuildInfoLiquidacionCompra(document),
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
                liquidacionCompra);
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
            new XElement("codDoc", "03"),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoLiquidacionCompra(Document document)
    {
        var issuer = document.IssuerInfo;
        var provider = document.BuyerInfo;

        var totalSinImpuestos = document.Items.Sum(i => i.Subtotal);
        var totalDescuento = document.Items.Sum(i => i.Discount);
        var totalIva = document.Items.Sum(i => i.Subtotal * i.TaxRate / 100m);

        // Determine retention amount (optional)
        decimal retentionValue = 0m;
        var hasRetention = issuer.TryGetValue("codigoRetencion", out var codigoRetencion)
            && !string.IsNullOrWhiteSpace(codigoRetencion);

        if (hasRetention && issuer.TryGetValue("valorRetencion", out var valorRetencionStr))
        {
            decimal.TryParse(valorRetencionStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out retentionValue);
        }

        var importeTotal = totalSinImpuestos + totalIva - retentionValue;

        var dirEstablecimiento = issuer.GetValueOrDefault("dirEstablecimiento")
                                 ?? issuer.GetValueOrDefault("dirMatriz");

        var infoLiquidacion = new XElement("infoLiquidacionCompra",
            new XElement("fechaEmision", GetRequiredValue(issuer, "fechaEmision")),
            OptionalElement("dirEstablecimiento", dirEstablecimiento),
            OptionalElement("obligadoContabilidad", issuer.GetValueOrDefault("obligadoContabilidad")),
            new XElement("tipoIdentificacionProveedor", GetRequiredValue(provider, "tipoIdentificacionProveedor")),
            new XElement("razonSocialProveedor", GetRequiredValue(provider, "razonSocialProveedor")),
            new XElement("identificacionProveedor", GetRequiredValue(provider, "identificacionProveedor")),
            new XElement("direccionProveedor", GetRequiredValue(provider, "direccionProveedor")),
            new XElement("totalSinImpuestos", totalSinImpuestos.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("totalDescuento", totalDescuento.ToString("F2", CultureInfo.InvariantCulture)),
            BuildTotalConImpuestos(document));

        // Emit optional retention block only when codigoRetencion is present
        if (hasRetention)
        {
            infoLiquidacion.Add(new XElement("retencion",
                new XElement("codigo", codigoRetencion),
                new XElement("codigoPorcentaje", codigoRetencion),
                new XElement("tarifa", issuer.GetValueOrDefault("porcentajeRetencion") ?? "0"),
                new XElement("valor", issuer.GetValueOrDefault("valorRetencion") ?? "0.00")));
        }

        infoLiquidacion.Add(
            new XElement("importeTotal", importeTotal.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("moneda", "DOLAR"),
            BuildPagos(importeTotal));

        return infoLiquidacion;
    }

    private static XElement BuildTotalConImpuestos(Document document)
    {
        var taxGroups = document.Items
            .GroupBy(i => new { i.TaxCode, i.TaxPercentageCode, i.TaxRate })
            .Select(g => new
            {
                g.Key.TaxCode,
                g.Key.TaxPercentageCode,
                g.Key.TaxRate,
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
            var detalle = new XElement("detalle",
                new XElement("codigoPrincipal", item.MainCode),
                OptionalElement("codigoAuxiliar", item.AuxiliaryCode),
                new XElement("descripcion", item.Description),
                new XElement("cantidad", item.Quantity.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement("precioUnitario", item.UnitPrice.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement("descuento", item.Discount.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("precioTotalSinImpuesto", item.Subtotal.ToString("F2", CultureInfo.InvariantCulture)),
                BuildImpuestosDetalle(item));

            detalles.Add(detalle);
        }

        return detalles;
    }

    private static XElement BuildImpuestosDetalle(DocumentItem item)
    {
        var taxValue = item.Subtotal * item.TaxRate / 100m;

        return new XElement("impuestos",
            new XElement("impuesto",
                new XElement("codigo", item.TaxCode),
                new XElement("codigoPorcentaje", item.TaxPercentageCode),
                new XElement("tarifa", item.TaxRate.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("baseImponible", item.Subtotal.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("valor", taxValue.ToString("F2", CultureInfo.InvariantCulture))));
    }

    private static XElement? BuildInfoAdicional(Document document)
    {
        var provider = document.BuyerInfo;
        var additionalFields = new List<(string Name, string? Value)>
        {
            ("email", provider.GetValueOrDefault("email")),
            ("telefono", provider.GetValueOrDefault("telefono"))
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

    private static XElement BuildPagos(decimal importeTotal)
    {
        return new XElement("pagos",
            new XElement("pago",
                new XElement("formaPago", "20"),
                new XElement("total", importeTotal.ToString("F2", CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Validates that all required fields from LiquidacionCompraConstants are present
    /// in the document before XML generation proceeds.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in LiquidacionCompraConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in LiquidacionCompraConstants.RequiredProviderFields)
        {
            if (!document.BuyerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"provider.{field}");
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
