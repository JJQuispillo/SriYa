using Qora.Billing.Domain.Entities;
using System.Text;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Generates a professional HTML email body for an authorized billing document.
/// </summary>
public static class HtmlEmailTemplate
{
    public static string Build(Document document)
    {
        var issuer = document.IssuerInfo;
        var buyer = document.BuyerInfo;

        var documentTypeLabel = document.DocumentType switch
        {
            Domain.Enums.DocumentType.Factura => "Factura Electrónica",
            Domain.Enums.DocumentType.NotaCredito => "Nota de Crédito",
            Domain.Enums.DocumentType.NotaDebito => "Nota de Débito",
            Domain.Enums.DocumentType.LiquidacionCompra => "Liquidación de Compra",
            Domain.Enums.DocumentType.GuiaRemision => "Guía de Remisión",
            Domain.Enums.DocumentType.ComprobanteRetencion => "Comprobante de Retención",
            _ => "Comprobante Electrónico"
        };

        var accessKey = document.AccessKey?.Value ?? "—";
        var authNumber = document.SriAuthorizationNumber ?? "—";
        var authDate = document.SriAuthorizationDate?.ToString("dd/MM/yyyy HH:mm") ?? "—";
        var issueDate = document.SriAuthorizationDate?.ToString("dd/MM/yyyy") ?? "—";

        var emisorRuc = issuer.GetValueOrDefault("ruc", issuer.GetValueOrDefault("identificacion", "—"));
        var emisorRazon = issuer.GetValueOrDefault("razonSocial", issuer.GetValueOrDefault("razon_social", "—"));

        var compradorNombre = buyer.GetValueOrDefault("razonSocialComprador",
                             buyer.GetValueOrDefault("razonSocial",
                             buyer.GetValueOrDefault("razonSocialDestinatario", "—")));
        var compradorId = buyer.GetValueOrDefault("identificacionComprador",
                         buyer.GetValueOrDefault("identificacion", "—"));

        decimal subtotal = 0m;
        decimal totalIva = 0m;

        foreach (var item in document.Items)
        {
            var itemIva = item.Subtotal * item.TaxRate / 100m;
            subtotal += item.Subtotal;
            totalIva += itemIva;
        }

        var total = subtotal + totalIva;

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>");
        sb.Append("<html lang=\"es\">");
        sb.Append("<head>");
        sb.Append("<meta charset=\"UTF-8\" />");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.Append($"<title>{documentTypeLabel}</title>");
        sb.Append("<style>");
        sb.Append("body { font-family: 'Segoe UI', Arial, sans-serif; background: #f4f6f8; margin: 0; padding: 0; }");
        sb.Append(".container { max-width: 600px; margin: 32px auto; background: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,.1); }");
        sb.Append(".header { background: #1a56db; color: #fff; padding: 28px 32px; }");
        sb.Append(".header h1 { margin: 0; font-size: 22px; font-weight: 700; }");
        sb.Append(".header p { margin: 4px 0 0; font-size: 14px; opacity: .85; }");
        sb.Append(".body { padding: 28px 32px; }");
        sb.Append(".info-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px 24px; margin-bottom: 24px; }");
        sb.Append(".info-item label { display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; color: #6b7280; margin-bottom: 2px; }");
        sb.Append(".info-item span { font-size: 14px; color: #111827; }");
        sb.Append(".totals { background: #f9fafb; border-radius: 6px; padding: 16px 20px; margin-bottom: 24px; }");
        sb.Append(".totals table { width: 100%; border-collapse: collapse; }");
        sb.Append(".totals td { padding: 4px 0; font-size: 14px; color: #374151; }");
        sb.Append(".totals td:last-child { text-align: right; font-weight: 500; }");
        sb.Append(".grand-total td { font-size: 16px; font-weight: 700; color: #1a56db; border-top: 1px solid #e5e7eb; padding-top: 8px; }");
        sb.Append(".footer { background: #f9fafb; padding: 16px 32px; text-align: center; font-size: 12px; color: #9ca3af; border-top: 1px solid #e5e7eb; }");
        sb.Append(".badge { display: inline-block; background: #d1fae5; color: #065f46; padding: 2px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; margin-top: 4px; }");
        sb.Append("</style>");
        sb.Append("</head>");
        sb.Append("<body>");
        sb.Append("<div class=\"container\">");
        sb.Append("<div class=\"header\">");
        sb.Append($"<h1>{documentTypeLabel}</h1>");
        sb.Append("<p>Autorizado por el SRI</p>");
        sb.Append("</div>");
        sb.Append("<div class=\"body\">");
        sb.Append("<div class=\"info-grid\">");
        sb.Append($"<div class=\"info-item\"><label>Clave de Acceso</label><span style=\"font-size:11px;word-break:break-all;\">{accessKey}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>N&#176; Autorización</label><span>{authNumber}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Fecha Autorización</label><span>{authDate}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Fecha Emisión</label><span>{issueDate}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Emisor (RUC)</label><span>{emisorRuc}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Razón Social Emisor</label><span>{emisorRazon}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Comprador / Receptor</label><span>{compradorNombre}</span></div>");
        sb.Append($"<div class=\"info-item\"><label>Identificación Comprador</label><span>{compradorId}</span></div>");
        sb.Append("</div>");
        sb.Append("<div class=\"totals\">");
        sb.Append("<table>");
        sb.Append($"<tr><td>Subtotal (sin IVA)</td><td>${subtotal:N2}</td></tr>");
        sb.Append($"<tr><td>IVA</td><td>${totalIva:N2}</td></tr>");
        sb.Append($"<tr class=\"grand-total\"><td>TOTAL</td><td>${total:N2}</td></tr>");
        sb.Append("</table>");
        sb.Append("</div>");
        sb.Append("<p style=\"font-size:13px;color:#6b7280;\">Adjunto encontrará el RIDE (Representación Impresa del Documento Electrónico) en formato PDF.<br/>El XML autorizado también está disponible como adjunto.</p>");
        sb.Append("<span class=\"badge\">&#10003; Documento Autorizado por el SRI</span>");
        sb.Append("</div>");
        sb.Append("<div class=\"footer\">");
        sb.Append("Este email fue generado automáticamente por <strong>Qora Billing</strong>.<br/>Por favor no responda a este mensaje.");
        sb.Append("</div>");
        sb.Append("</div>");
        sb.Append("</body>");
        sb.Append("</html>");

        return sb.ToString();
    }
}
