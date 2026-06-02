using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Genera el XML sin firmar para documentos Factura siguiendo el esquema XSD v2.1.0 del SRI.
/// Usa System.Xml.Linq para una construcción de XML más limpia.
/// </summary>
public class FacturaXmlBuilder : IXmlGenerator
{
    public Task<string> GenerateXmlAsync(Document document, CancellationToken cancellationToken = default)
    {
        if (document.DocumentType != DocumentType.Factura)
            throw new DocumentValidationException($"FacturaXmlBuilder solo soporta documentos de tipo Factura, se recibió {document.DocumentType}.");

        var xml = BuildFacturaXml(document);
        return Task.FromResult(xml);
    }

    private string BuildFacturaXml(Document document)
    {
        ValidateRequiredFields(document);

        var factura = new XElement("factura",
            new XAttribute("id", "comprobante"),
            new XAttribute("version", "2.1.0"),
            BuildInfoTributaria(document),
            BuildInfoFactura(document),
            BuildDetalles(document),
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
                factura);
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
            new XElement("codDoc", ((int)DocumentType.Factura).ToString("D2")),
            new XElement("estab", GetRequiredValue(issuer, "estab")),
            new XElement("ptoEmi", GetRequiredValue(issuer, "ptoEmi")),
            new XElement("secuencial", GetRequiredValue(issuer, "secuencial")),
            new XElement("dirMatriz", GetRequiredValue(issuer, "dirMatriz")));
    }

    private static XElement BuildInfoFactura(Document document)
    {
        var issuer = document.IssuerInfo;
        var buyer = document.BuyerInfo;

        var totalSinImpuestos = document.Items.Sum(i => i.Subtotal);
        var totalDescuento = document.Items.Sum(i => i.Discount);

        var infoFactura = new XElement("infoFactura",
            new XElement("fechaEmision", GetRequiredValue(issuer, "fechaEmision")),
            OptionalElement("dirEstablecimiento", issuer.GetValueOrDefault("dirEstablecimiento")),
            OptionalElement("contribuyenteEspecial", issuer.GetValueOrDefault("contribuyenteEspecial")),
            new XElement("obligadoContabilidad", issuer.GetValueOrDefault("obligadoContabilidad") ?? "NO"),
            new XElement("tipoIdentificacionComprador", GetRequiredValue(buyer, "tipoIdentificacion")),
            OptionalElement("guiaRemision", buyer.GetValueOrDefault("guiaRemision")),
            new XElement("razonSocialComprador", GetRequiredValue(buyer, "razonSocial")),
            new XElement("identificacionComprador", GetRequiredValue(buyer, "identificacion")),
            OptionalElement("direccionComprador", buyer.GetValueOrDefault("direccion")),
            new XElement("totalSinImpuestos", totalSinImpuestos.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("totalDescuento", totalDescuento.ToString("F2", CultureInfo.InvariantCulture)),
            BuildTotalConImpuestos(document),
            new XElement("propina", "0.00"),
            new XElement("importeTotal", CalculateImporteTotal(document).ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("moneda", "DOLAR"),
            BuildPagos(document));

        return infoFactura;
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

    private static XElement BuildPagos(Document document)
    {
        var buyer = document.BuyerInfo;
        var formaPago = buyer.GetValueOrDefault("formaPago") ?? "01"; // 01 = Efectivo
        var total = CalculateImporteTotal(document);

        return new XElement("pagos",
            new XElement("pago",
                new XElement("formaPago", formaPago),
                new XElement("total", total.ToString("F2", CultureInfo.InvariantCulture))));
    }

    private static decimal CalculateImporteTotal(Document document)
    {
        var subtotal = document.Items.Sum(i => i.Subtotal);
        var totalTax = document.Items.Sum(i => i.Subtotal * i.TaxRate / 100m);
        return subtotal + totalTax;
    }

    /// <summary>
    /// Valida que todos los campos obligatorios de FacturaConstants estén presentes
    /// en el documento antes de proceder con la generación del XML.
    /// </summary>
    private static void ValidateRequiredFields(Document document)
    {
        var missingFields = new List<string>();

        foreach (var field in FacturaConstants.AllRequiredIssuerFields)
        {
            if (!document.IssuerInfo.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add($"issuer.{field}");
            }
        }

        foreach (var field in FacturaConstants.RequiredBuyerFields)
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
