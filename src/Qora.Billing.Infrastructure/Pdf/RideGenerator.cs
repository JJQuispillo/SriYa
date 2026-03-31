using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Qora.Billing.Domain.Interfaces;
using BillingDocument = Qora.Billing.Domain.Entities.Document;

namespace Qora.Billing.Infrastructure.Pdf;

/// <summary>
/// Generates RIDE (Representacion Impresa del Documento Electronico) PDF documents
/// following SRI visual standards for Ecuadorian electronic invoicing.
/// </summary>
public sealed class RideGenerator : IRideGenerator
{
    public Task<byte[]> GeneratePdfAsync(BillingDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pdfBytes = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(30);
                page.MarginVertical(20);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().Element(header => ComposeHeader(header, document));
                page.Content().Element(content => ComposeContent(content, document));
                page.Footer().Element(ComposeFooter);
            });
        }).GeneratePdf();

        return Task.FromResult(pdfBytes);
    }

    private static void ComposeHeader(IContainer container, BillingDocument document)
    {
        container.Column(column =>
        {
            column.Spacing(5);

            // Two-column header: company info (left) + document info (right)
            column.Item().Row(row =>
            {
                // Left: Company / Issuer info
                row.RelativeItem(5).Border(1).Padding(8).Column(left =>
                {
                    left.Spacing(3);

                    // Logo placeholder
                    left.Item().Height(40).Width(120)
                        .Background(Colors.Grey.Lighten3)
                        .AlignCenter().AlignMiddle()
                        .Text("LOGO").FontSize(10).Bold();

                    left.Item().Text(GetValue(document.IssuerInfo, "razonSocial"))
                        .Bold().FontSize(10);

                    var tradeName = GetValue(document.IssuerInfo, "nombreComercial");
                    if (!string.IsNullOrEmpty(tradeName))
                        left.Item().Text(tradeName).FontSize(8);

                    var address = GetValue(document.IssuerInfo, "direccion", "direccionMatriz");
                    if (!string.IsNullOrEmpty(address))
                        left.Item().Text($"Dir: {address}");

                    left.Item().Text($"Obligado a llevar contabilidad: {GetValue(document.IssuerInfo, "obligadoContabilidad", defaultValue: "SI")}");
                });

                row.ConstantItem(10); // spacer

                // Right: Document info
                row.RelativeItem(4).Border(1).Padding(8).Column(right =>
                {
                    right.Spacing(3);

                    right.Item().Text($"R.U.C.: {GetValue(document.IssuerInfo, "ruc")}")
                        .Bold().FontSize(10);

                    right.Item().Text(GetDocumentTypeName(document.DocumentType))
                        .Bold().FontSize(10);

                    var establishment = GetValue(document.IssuerInfo, "estab", defaultValue: "001");
                    var emissionPoint = GetValue(document.IssuerInfo, "ptoEmi", defaultValue: "001");
                    var sequential = GetValue(document.IssuerInfo, "secuencial", defaultValue: "000000001");
                    right.Item().Text($"No. {establishment}-{emissionPoint}-{sequential}")
                        .Bold().FontSize(10);

                    // Environment
                    var environment = GetValue(document.IssuerInfo, "ambiente", defaultValue: "1");
                    right.Item().Text($"AMBIENTE: {(environment == "2" ? "PRODUCCION" : "PRUEBAS")}")
                        .FontSize(7);

                    // Emission type
                    right.Item().Text("EMISION: NORMAL").FontSize(7);

                    // Access Key
                    right.Item().PaddingTop(5).Text("CLAVE DE ACCESO:").Bold().FontSize(7);
                    var accessKeyValue = document.AccessKey?.Value ?? string.Empty;
                    right.Item().Text(accessKeyValue).FontSize(7);

                    // Barcode for access key (visual Code 128 representation)
                    if (!string.IsNullOrEmpty(accessKeyValue))
                    {
                        right.Item().PaddingTop(3).Element(c => ComposeBarcode(c, accessKeyValue));
                    }

                    // Authorization
                    if (!string.IsNullOrEmpty(document.SriAuthorizationNumber))
                    {
                        right.Item().PaddingTop(3).Text("NUMERO DE AUTORIZACION:").Bold().FontSize(7);
                        right.Item().Text(document.SriAuthorizationNumber).FontSize(7);
                        if (document.SriAuthorizationDate.HasValue)
                            right.Item().Text($"FECHA: {document.SriAuthorizationDate.Value:dd/MM/yyyy HH:mm:ss}").FontSize(7);
                    }
                });
            });
        });
    }

    /// <summary>
    /// Renders a Code 128B barcode by grouping consecutive same-value modules
    /// into single bars/spaces for efficient rendering.
    /// </summary>
    private static void ComposeBarcode(IContainer container, string data)
    {
        var encoded = EncodeCode128B(data);
        if (encoded.Length == 0) return;

        // Group consecutive same-value bits into runs to reduce element count
        var runs = new List<(bool isBar, int count)>();
        var i = 0;
        while (i < encoded.Length)
        {
            var current = encoded[i];
            var runLength = 0;
            while (i < encoded.Length && encoded[i] == current)
            {
                runLength++;
                i++;
            }
            runs.Add((current == '1', runLength));
        }

        // Calculate module width to fit within available space
        // Total modules in encoded string, fit to ~200pt max width
        var totalModules = encoded.Length;
        var moduleWidth = Math.Min(0.5f, 200f / totalModules);

        container.Height(30).Row(row =>
        {
            foreach (var (isBar, count) in runs)
            {
                var width = moduleWidth * count;
                if (isBar)
                    row.ConstantItem(width).Background(Colors.Black).Height(30);
                else
                    row.ConstantItem(width).Background(Colors.White).Height(30);
            }
        });
    }

    private static void ComposeContent(IContainer container, BillingDocument document)
    {
        container.PaddingTop(10).Column(column =>
        {
            switch (document.DocumentType)
            {
                case Domain.Enums.DocumentType.Factura:
                    ComposeFacturaContent(column, document);
                    break;
                case Domain.Enums.DocumentType.NotaCredito:
                    ComposeNotaCreditoContent(column, document);
                    break;
                case Domain.Enums.DocumentType.NotaDebito:
                    ComposeNotaDebitoContent(column, document);
                    break;
                case Domain.Enums.DocumentType.LiquidacionCompra:
                    ComposeLiquidacionCompraContent(column, document);
                    break;
                case Domain.Enums.DocumentType.GuiaRemision:
                    ComposeGuiaRemisionContent(column, document);
                    break;
                case Domain.Enums.DocumentType.ComprobanteRetencion:
                    ComposeCompRetencionContent(column, document);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Document type {document.DocumentType} is not supported for RIDE generation.");
            }
        });
    }

    private static void ComposeFacturaContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Buyer info section
        column.Item().Element(c => ComposeBuyerInfo(c, document));

        // Items table
        column.Item().Element(c => ComposeItemsTable(c, document));

        // Totals section
        column.Item().Element(c => ComposeTotals(c, document));

        // Additional info section
        column.Item().Element(c => ComposeAdditionalInfo(c, document));
    }

    private static void ComposeNotaCreditoContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Buyer info section
        column.Item().Element(c => ComposeBuyerInfo(c, document));

        // Items table
        column.Item().Element(c => ComposeItemsTable(c, document));

        // Document reference block (documento que se modifica)
        column.Item().Element(c => ComposeDocReference(c, document));

        // Totals section
        column.Item().Element(c => ComposeTotals(c, document));
    }

    private static void ComposeNotaDebitoContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Buyer info section
        column.Item().Element(c => ComposeBuyerInfo(c, document));

        // Motivos table (Razon, Valor) — NO product columns
        column.Item().Element(c => ComposeMotivosTable(c, document));

        // Totals section — sum of motivos valores
        column.Item().Element(c => ComposeNotaDebitoTotals(c, document));
    }

    private static void ComposeLiquidacionCompraContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Provider info (instead of buyer info)
        column.Item().Element(c => ComposeProviderInfo(c, document));

        // Items table (same as Factura)
        column.Item().Element(c => ComposeItemsTable(c, document));

        // Totals section
        column.Item().Element(c => ComposeTotals(c, document));
    }

    private static void ComposeGuiaRemisionContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Transporter section
        column.Item().Element(c => ComposeTransporterInfo(c, document));

        // Destinatario section
        column.Item().Element(c => ComposeDestinatarioInfo(c, document));

        // Items table — only Codigo, Descripcion, Cantidad (NO price/totals columns)
        column.Item().Element(c => ComposeGuiaDetallesTable(c, document));

        // NO totals section for Guia de Remision
    }

    private static void ComposeCompRetencionContent(ColumnDescriptor column, BillingDocument document)
    {
        column.Spacing(8);

        // Subject info (sujeto retenido)
        column.Item().Element(c => ComposeBuyerInfo(c, document));

        // Retenciones table
        column.Item().Element(c => ComposeRetencioneTable(c, document));

        // Totals: total retenido
        column.Item().Element(c => ComposeRetencionTotals(c, document));
    }

    private static void ComposeBuyerInfo(IContainer container, BillingDocument document)
    {
        container.Border(1).Padding(6).Column(column =>
        {
            column.Spacing(2);

            var buyerName = GetValue(document.BuyerInfo, "razonSocial", "nombre");
            var buyerId = GetValue(document.BuyerInfo, "ruc", "identificacion", "cedula");
            var buyerAddress = GetValue(document.BuyerInfo, "direccion");
            var buyerPhone = GetValue(document.BuyerInfo, "telefono");
            var buyerEmail = GetValue(document.BuyerInfo, "email", "correo");

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Razon Social / Nombres: ").Bold();
                    text.Span(buyerName ?? string.Empty);
                });
            });

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("RUC / CI: ").Bold();
                    text.Span(buyerId ?? string.Empty);
                });
                row.RelativeItem().Text(text =>
                {
                    text.Span("Fecha Emision: ").Bold();
                    text.Span(document.CreatedAt.ToString("dd/MM/yyyy"));
                });
            });

            if (!string.IsNullOrEmpty(buyerAddress))
            {
                column.Item().Text(text =>
                {
                    text.Span("Direccion: ").Bold();
                    text.Span(buyerAddress);
                });
            }

            column.Item().Row(row =>
            {
                if (!string.IsNullOrEmpty(buyerPhone))
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Telefono: ").Bold();
                        text.Span(buyerPhone);
                    });
                }
                if (!string.IsNullOrEmpty(buyerEmail))
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Email: ").Bold();
                        text.Span(buyerEmail);
                    });
                }
            });
        });
    }

    private static void ComposeItemsTable(IContainer container, BillingDocument document)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(30);   // Cod. Principal
                columns.ConstantColumn(30);   // Cod. Auxiliar
                columns.ConstantColumn(30);   // Cant.
                columns.RelativeColumn(3);    // Descripcion
                columns.ConstantColumn(55);   // Precio Unitario
                columns.ConstantColumn(45);   // Descuento
                columns.ConstantColumn(55);   // Total
            });

            // Header row
            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Cod.").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Aux.").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Cant.").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Descripcion").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("P. Unit.").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Desc.").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Total").Bold().FontSize(7);
            });

            // Item rows
            foreach (var item in document.Items)
            {
                table.Cell().Border(0.5f).Padding(2).Text(item.MainCode).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).Text(item.AuxiliaryCode ?? "").FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight().Text(item.Quantity.ToString("F2")).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).Text(item.Description).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight().Text(item.UnitPrice.ToString("F2")).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight().Text(item.Discount.ToString("F2")).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight().Text(item.Subtotal.ToString("F2")).FontSize(7);
            }
        });
    }

    private static void ComposeTotals(IContainer container, BillingDocument document)
    {
        // Calculate tax breakdowns from items
        var items = document.Items;
        var subtotal0 = items.Where(i => i.TaxRate == 0).Sum(i => i.Subtotal);
        var subtotal5 = items.Where(i => i.TaxRate == 5).Sum(i => i.Subtotal);
        var subtotal15 = items.Where(i => i.TaxRate == 15).Sum(i => i.Subtotal);
        // For any other rates, group them
        var subtotalOther = items.Where(i => i.TaxRate != 0 && i.TaxRate != 5 && i.TaxRate != 15).Sum(i => i.Subtotal);

        var totalDiscount = items.Sum(i => i.Discount);
        var iva5 = subtotal5 * 0.05m;
        var iva15 = subtotal15 * 0.15m;
        var ivaOther = items.Where(i => i.TaxRate != 0 && i.TaxRate != 5 && i.TaxRate != 15)
            .Sum(i => i.Subtotal * (i.TaxRate / 100m));
        var subtotalSinImpuestos = items.Sum(i => i.Subtotal);
        var totalIva = iva5 + iva15 + ivaOther;
        var total = subtotalSinImpuestos + totalIva;

        container.Row(row =>
        {
            // Additional info space (left)
            row.RelativeItem(5);

            row.ConstantItem(10); // spacer

            // Totals (right)
            row.RelativeItem(4).Border(1).Padding(5).Column(totals =>
            {
                totals.Spacing(2);

                AddTotalRow(totals, "SUBTOTAL SIN IMPUESTOS", subtotalSinImpuestos);
                AddTotalRow(totals, "SUBTOTAL 0%", subtotal0);
                if (subtotal5 > 0)
                    AddTotalRow(totals, "SUBTOTAL 5%", subtotal5);
                AddTotalRow(totals, "SUBTOTAL 15%", subtotal15 + subtotalOther);
                AddTotalRow(totals, "DESCUENTO", totalDiscount);
                if (iva5 > 0)
                    AddTotalRow(totals, "IVA 5%", iva5);
                AddTotalRow(totals, "IVA 15%", iva15 + ivaOther);
                AddTotalRow(totals, "VALOR TOTAL", total, bold: true);
            });
        });
    }

    private static void AddTotalRow(ColumnDescriptor column, string label, decimal value, bool bold = false)
    {
        column.Item().Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                if (bold)
                    text.Span(label).Bold();
                else
                    text.Span(label);
            });
            row.ConstantItem(60).AlignRight().Text(text =>
            {
                if (bold)
                    text.Span(value.ToString("F2")).Bold();
                else
                    text.Span(value.ToString("F2"));
            });
        });
    }

    private static void ComposeDocReference(IContainer container, BillingDocument document)
    {
        var issuer = document.IssuerInfo;
        var codDoc = GetValue(issuer, "codDocModificado");
        var numDoc = GetValue(issuer, "numDocModificado");
        var fechaEmision = GetValue(issuer, "fechaEmisionDocSustento");
        var motivo = GetValue(issuer, "motivo", "razonModificacion");

        container.Border(1).Padding(6).Column(col =>
        {
            col.Spacing(2);
            col.Item().Text("Documento que se modifica").Bold().FontSize(8);
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Tipo Doc. Modificado: ").Bold();
                    text.Span(codDoc);
                });
                row.RelativeItem().Text(text =>
                {
                    text.Span("Nro. Doc. Modificado: ").Bold();
                    text.Span(numDoc);
                });
            });
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Fecha Emision: ").Bold();
                    text.Span(fechaEmision);
                });
            });
            if (!string.IsNullOrEmpty(motivo))
            {
                col.Item().Text(text =>
                {
                    text.Span("Razon de Modificacion: ").Bold();
                    text.Span(motivo);
                });
            }
        });
    }

    private static void ComposeMotivosTable(IContainer container, BillingDocument document)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);   // Razon
                columns.ConstantColumn(80);  // Valor
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Razon").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Valor").Bold().FontSize(7);
            });

            foreach (var item in document.Items)
            {
                table.Cell().Border(0.5f).Padding(2).Text(item.Description).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight()
                    .Text(item.UnitPrice.ToString("F2")).FontSize(7);
            }
        });
    }

    private static void ComposeNotaDebitoTotals(IContainer container, BillingDocument document)
    {
        var totalValor = document.Items.Sum(i => i.UnitPrice);

        container.Row(row =>
        {
            row.RelativeItem(5);
            row.ConstantItem(10);
            row.RelativeItem(4).Border(1).Padding(5).Column(totals =>
            {
                totals.Spacing(2);
                AddTotalRow(totals, "VALOR TOTAL", totalValor, bold: true);
            });
        });
    }

    private static void ComposeProviderInfo(IContainer container, BillingDocument document)
    {
        container.Border(1).Padding(6).Column(column =>
        {
            column.Spacing(2);

            var providerName = GetValue(document.BuyerInfo, "razonSocialProveedor", "razonSocial");
            var providerId = GetValue(document.BuyerInfo, "identificacionProveedor", "identificacion", "ruc");
            var providerAddress = GetValue(document.BuyerInfo, "direccionProveedor", "direccion");

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Proveedor: ").Bold();
                    text.Span(providerName ?? string.Empty);
                });
            });

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("RUC / CI: ").Bold();
                    text.Span(providerId ?? string.Empty);
                });
                row.RelativeItem().Text(text =>
                {
                    text.Span("Fecha Emision: ").Bold();
                    text.Span(document.CreatedAt.ToString("dd/MM/yyyy"));
                });
            });

            if (!string.IsNullOrEmpty(providerAddress))
            {
                column.Item().Text(text =>
                {
                    text.Span("Direccion: ").Bold();
                    text.Span(providerAddress);
                });
            }
        });
    }

    private static void ComposeTransporterInfo(IContainer container, BillingDocument document)
    {
        var issuer = document.IssuerInfo;
        var ruc = GetValue(issuer, "rucTransportista");
        var nombre = GetValue(issuer, "razonSocialTransportista");
        var placa = GetValue(issuer, "placa");
        var fechaInicio = GetValue(issuer, "fechaInicioTransporte");
        var fechaFin = GetValue(issuer, "fechaFinTransporte");

        container.Border(1).Padding(6).Column(col =>
        {
            col.Spacing(2);
            col.Item().Text("Datos del Transportista").Bold().FontSize(8);
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("RUC Transportista: ").Bold();
                    text.Span(ruc);
                });
                row.RelativeItem().Text(text =>
                {
                    text.Span("Razon Social: ").Bold();
                    text.Span(nombre);
                });
            });
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Placa: ").Bold();
                    text.Span(placa);
                });
                if (!string.IsNullOrEmpty(fechaInicio))
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Fecha Inicio Transporte: ").Bold();
                        text.Span(fechaInicio);
                    });
                }
                if (!string.IsNullOrEmpty(fechaFin))
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Fecha Fin Transporte: ").Bold();
                        text.Span(fechaFin);
                    });
                }
            });
        });
    }

    private static void ComposeDestinatarioInfo(IContainer container, BillingDocument document)
    {
        var buyer = document.BuyerInfo;
        var identificacion = GetValue(buyer, "identificacionDestinatario", "identificacion", "ruc");
        var nombre = GetValue(buyer, "razonSocialDestinatario", "razonSocial");
        var direccion = GetValue(buyer, "dirDestinatario", "direccion");
        var motivo = GetValue(buyer, "motivoTraslado");

        container.Border(1).Padding(6).Column(col =>
        {
            col.Spacing(2);
            col.Item().Text("Datos del Destinatario").Bold().FontSize(8);
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.Span("Identificacion: ").Bold();
                    text.Span(identificacion);
                });
                row.RelativeItem().Text(text =>
                {
                    text.Span("Razon Social: ").Bold();
                    text.Span(nombre);
                });
            });
            if (!string.IsNullOrEmpty(direccion))
            {
                col.Item().Text(text =>
                {
                    text.Span("Direccion: ").Bold();
                    text.Span(direccion);
                });
            }
            if (!string.IsNullOrEmpty(motivo))
            {
                col.Item().Text(text =>
                {
                    text.Span("Motivo de Traslado: ").Bold();
                    text.Span(motivo);
                });
            }
        });
    }

    private static void ComposeGuiaDetallesTable(IContainer container, BillingDocument document)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(60);  // Codigo
                columns.RelativeColumn(3);   // Descripcion
                columns.ConstantColumn(50);  // Cantidad
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Codigo").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Descripcion").Bold().FontSize(7);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(3)
                    .Text("Cantidad").Bold().FontSize(7);
            });

            foreach (var item in document.Items)
            {
                table.Cell().Border(0.5f).Padding(2).Text(item.MainCode).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).Text(item.Description).FontSize(7);
                table.Cell().Border(0.5f).Padding(2).AlignRight()
                    .Text(item.Quantity.ToString("F2")).FontSize(7);
            }
        });
    }

    private static void ComposeRetencioneTable(IContainer container, BillingDocument document)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(40);  // Cod. Doc. Sustento
                columns.ConstantColumn(70);  // Num. Doc. Sustento
                columns.ConstantColumn(50);  // Fecha Emision
                columns.RelativeColumn(2);   // Num. Aut.
                columns.ConstantColumn(35);  // Cod. Impuesto
                columns.ConstantColumn(40);  // % Retener
                columns.ConstantColumn(55);  // Base Imponible
                columns.ConstantColumn(55);  // Valor Retenido
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Cod.\nDoc.").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Num. Doc. Sustento").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Fecha\nEmision").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Num. Aut.").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Cod.\nImp.").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("% Ret.").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Base\nImpon.").Bold().FontSize(6);
                header.Cell().Background(Colors.Grey.Lighten2).Border(0.5f).Padding(2)
                    .Text("Valor\nReten.").Bold().FontSize(6);
            });

            foreach (var item in document.Items)
            {
                var parts = ParseAuxiliaryCode(item.AuxiliaryCode);
                var codDocSustento = parts[0].Length > 0 ? parts[0] : item.MainCode;
                var numDocSustento = parts[1];
                var fechaEmisionDoc = parts[2];
                var numAutDoc = parts[3];
                var baseImponible = item.Quantity * item.UnitPrice;
                var valorRetenido = baseImponible * item.TaxRate / 100m;

                table.Cell().Border(0.5f).Padding(2).Text(codDocSustento).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).Text(numDocSustento).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).Text(fechaEmisionDoc).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).Text(numAutDoc).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).AlignRight().Text(item.TaxCode).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).AlignRight()
                    .Text(item.TaxRate.ToString("F2")).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).AlignRight()
                    .Text(baseImponible.ToString("F2")).FontSize(6);
                table.Cell().Border(0.5f).Padding(2).AlignRight()
                    .Text(valorRetenido.ToString("F2")).FontSize(6);
            }
        });
    }

    private static void ComposeRetencionTotals(IContainer container, BillingDocument document)
    {
        var totalRetenido = document.Items.Sum(i => i.Quantity * i.UnitPrice * i.TaxRate / 100m);

        container.Row(row =>
        {
            row.RelativeItem(5);
            row.ConstantItem(10);
            row.RelativeItem(4).Border(1).Padding(5).Column(totals =>
            {
                totals.Spacing(2);
                AddTotalRow(totals, "TOTAL RETENIDO", totalRetenido, bold: true);
            });
        });
    }

    /// <summary>
    /// Parses pipe-separated AuxiliaryCode for retenciones.
    /// Format: "codDocSustento|numDocSustento|fechaEmisionDocSustento|numAutDocSustento"
    /// Returns 4-element array; missing parts default to empty string.
    /// </summary>
    private static string[] ParseAuxiliaryCode(string? auxiliaryCode)
    {
        var result = new string[4] { "", "", "", "" };
        if (string.IsNullOrWhiteSpace(auxiliaryCode))
            return result;
        var parts = auxiliaryCode.Split('|');
        for (var i = 0; i < Math.Min(parts.Length, 4); i++)
            result[i] = parts[i] ?? "";
        return result;
    }

    private static void ComposeAdditionalInfo(IContainer container, BillingDocument document)
    {
        // Additional info from buyer (email, phone, etc.) displayed as key-value pairs
        var additionalPairs = new List<KeyValuePair<string, string>>();

        var email = GetValue(document.BuyerInfo, "email", "correo");
        if (!string.IsNullOrEmpty(email))
            additionalPairs.Add(new("Email", email));

        var phone = GetValue(document.BuyerInfo, "telefono");
        if (!string.IsNullOrEmpty(phone))
            additionalPairs.Add(new("Telefono", phone));

        // Include any issuer additional info keys not already used
        var knownIssuerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ruc", "razonSocial", "nombreComercial", "direccion", "direccionMatriz",
            "obligadoContabilidad", "estab", "ptoEmi", "secuencial", "ambiente"
        };
        foreach (var kvp in document.IssuerInfo.Where(k => !knownIssuerKeys.Contains(k.Key)))
            additionalPairs.Add(kvp);

        if (additionalPairs.Count == 0) return;

        container.Border(1).Padding(5).Column(col =>
        {
            col.Item().Text("Informacion Adicional").Bold().FontSize(8);
            col.Item().PaddingTop(3);
            foreach (var pair in additionalPairs)
            {
                col.Item().Text(text =>
                {
                    text.Span($"{pair.Key}: ").Bold().FontSize(7);
                    text.Span(pair.Value).FontSize(7);
                });
            }
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.AlignCenter().Text(text =>
        {
            text.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Medium));
            text.Span("Pagina ");
            text.CurrentPageNumber();
            text.Span(" de ");
            text.TotalPages();
        });
    }

    /// <summary>
    /// Encodes a string using Code 128B encoding (simplified).
    /// Returns a string of '0' (space) and '1' (bar) characters.
    /// </summary>
    internal static string EncodeCode128B(string data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;

        // Code 128B bar patterns — each symbol = 11 modules, stop = 13
        // Index 0-94 correspond to ASCII 32-126
        var code128Patterns = new[]
        {
            "11011001100", "11001101100", "11001100110", "10010011000", "10010001100", // 0-4
            "10001001100", "10011001000", "10011000100", "10001100100", "11001001000", // 5-9
            "11001000100", "11000100100", "10110011100", "10011011100", "10011001110", // 10-14
            "10111001100", "10011101100", "10011100110", "11001110010", "11001011100", // 15-19
            "11001001110", "11011100100", "11001110100", "11101101110", "11101001100", // 20-24
            "11100101100", "11100100110", "11101100100", "11100110100", "11100110010", // 25-29
            "11011011000", "11011000110", "11000110110", "10100011000", "10001011000", // 30-34
            "10001000110", "10110001000", "10001101000", "10001100010", "11010001000", // 35-39
            "11000101000", "11000100010", "10110111000", "10110001110", "10001101110", // 40-44
            "10111011000", "10111000110", "10001110110", "11101110110", "11010001110", // 45-49
            "11000101110", "11011101000", "11011100010", "11011101110", "11101011000", // 50-54
            "11101000110", "11100010110", "11101101000", "11101100010", "11100011010", // 55-59
            "11101111010", "11001000010", "11110001010", "10100110000", "10100001100", // 60-64
            "10010110000", "10010000110", "10000101100", "10000100110", "10110010000", // 65-69
            "10110000100", "10011010000", "10011000010", "10000110100", "10000110010", // 70-74
            "11000010010", "11001010000", "11110111010", "11000010100", "10001111010", // 75-79
            "10100111100", "10010111100", "10010011110", "10111100100", "10011110100", // 80-84
            "10011110010", "11110100100", "11110010100", "11110010010", "11011011110", // 85-89
            "11011110110", "11110110110", "10101111000", "10100011110", "10001011110", // 90-94
            "10111101000", "10111100010", "11110101000", "11110100010", "10111011110", // 95-99
            "10111101110", "11101011110", "11110101110", "11010000100", "11010010000", // 100-104
            "11010011100", "1100011101011"  // 105-106
        };

        var result = new System.Text.StringBuilder();

        // Start Code B (index 104)
        result.Append(code128Patterns[104]);

        var checksum = 104;
        var position = 1;

        foreach (var c in data)
        {
            var value = c - 32; // ASCII value - 32
            if (value < 0 || value > 94)
                value = 0; // fallback to space for unsupported chars

            result.Append(code128Patterns[value]);
            checksum += value * position;
            position++;
        }

        // Checksum character
        var checksumValue = checksum % 103;
        result.Append(code128Patterns[checksumValue]);

        // Stop pattern (index 106)
        result.Append(code128Patterns[106]);

        return result.ToString();
    }

    private static string GetDocumentTypeName(Domain.Enums.DocumentType documentType) => documentType switch
    {
        Domain.Enums.DocumentType.Factura => "FACTURA",
        Domain.Enums.DocumentType.LiquidacionCompra => "LIQUIDACION DE COMPRA",
        Domain.Enums.DocumentType.NotaCredito => "NOTA DE CREDITO",
        Domain.Enums.DocumentType.NotaDebito => "NOTA DE DEBITO",
        Domain.Enums.DocumentType.GuiaRemision => "GUIA DE REMISION",
        Domain.Enums.DocumentType.ComprobanteRetencion => "COMPROBANTE DE RETENCION",
        _ => "DOCUMENTO ELECTRONICO"
    };

    /// <summary>
    /// Gets a value from a dictionary, trying multiple keys in order.
    /// Returns the default value if none of the keys are found.
    /// </summary>
    private static string GetValue(Dictionary<string, string> dict, string key1,
        string? key2 = null, string? key3 = null, string defaultValue = "")
    {
        if (dict.TryGetValue(key1, out var v1) && !string.IsNullOrEmpty(v1)) return v1;
        if (key2 is not null && dict.TryGetValue(key2, out var v2) && !string.IsNullOrEmpty(v2)) return v2;
        if (key3 is not null && dict.TryGetValue(key3, out var v3) && !string.IsNullOrEmpty(v3)) return v3;
        return defaultValue;
    }
}
