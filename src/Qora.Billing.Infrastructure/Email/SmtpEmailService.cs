using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Orquesta el envío de email para los documentos de facturación autorizados.
/// Selecciona el proveedor SMTP correcto (Qora o Custom) según la configuración del tenant,
/// adjunta el RIDE PDF, y envuelve el envío en un pipeline de retry de Polly.
/// </summary>
public class SmtpEmailService(
    QoraEmailProvider qoraEmailProvider,
    CustomEmailProvider customEmailProvider,
    IRideGenerator rideGenerator,
    IOptions<QoraEmailSettings> qoraSettings,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private static readonly ResiliencePipeline RetryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2)
        })
        .Build();

    public async Task<bool> SendDocumentEmailAsync(
        Document document,
        Tenant tenant,
        CancellationToken cancellationToken = default)
    {
        if (!tenant.EmailEnabled)
        {
            logger.LogDebug("Email delivery disabled for tenant {TenantId}. Skipping.", tenant.Id);
            return false;
        }

        // Determinar el destinatario a partir del diccionario BuyerInfo
        var recipientEmail = document.BuyerInfo.GetValueOrDefault("correo",
                             document.BuyerInfo.GetValueOrDefault("email",
                             document.BuyerInfo.GetValueOrDefault("correoComprador", string.Empty)));

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            logger.LogWarning("No recipient email found in BuyerInfo for document {DocumentId}. Skipping email.", document.Id);
            return false;
        }

        var recipientName = document.BuyerInfo.GetValueOrDefault("razonSocialComprador",
                            document.BuyerInfo.GetValueOrDefault("razonSocial", recipientEmail));

        var documentTypeLabel = document.DocumentType switch
        {
            DocumentType.Factura => "Factura Electrónica",
            DocumentType.NotaCredito => "Nota de Crédito",
            DocumentType.NotaDebito => "Nota de Débito",
            DocumentType.LiquidacionCompra => "Liquidación de Compra",
            DocumentType.GuiaRemision => "Guía de Remisión",
            DocumentType.ComprobanteRetencion => "Comprobante de Retención",
            _ => "Comprobante Electrónico"
        };

        // Seleccionar la configuración y el proveedor según la elección de proveedor del tenant
        EmailConfiguration config;
        IEmailProvider provider;

        if (tenant.EmailProvider == EmailProvider.Custom)
        {
            config = EmailConfigurationFactory.ForCustom(tenant);
            provider = customEmailProvider;
        }
        else
        {
            config = EmailConfigurationFactory.ForQora(qoraSettings.Value);
            provider = qoraEmailProvider;
        }

        // Construir los adjuntos
        var attachments = new List<EmailAttachment>();

        // Adjunto del RIDE PDF
        try
        {
            var pdfBytes = await rideGenerator.GeneratePdfAsync(document, cancellationToken);
            var accessKey = document.AccessKey?.Value ?? document.Id.ToString();
            attachments.Add(new EmailAttachment
            {
                FileName = $"RIDE_{accessKey}.pdf",
                Content = pdfBytes,
                ContentType = "application/pdf"
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate RIDE PDF for email attachment on document {DocumentId}", document.Id);
        }

        // Adjunto del XML (XML firmado)
        if (!string.IsNullOrWhiteSpace(document.SignedXmlContent))
        {
            var accessKey = document.AccessKey?.Value ?? document.Id.ToString();
            attachments.Add(new EmailAttachment
            {
                FileName = $"XML_{accessKey}.xml",
                Content = System.Text.Encoding.UTF8.GetBytes(document.SignedXmlContent),
                ContentType = "application/xml"
            });
        }

        var emailMessage = new EmailMessage
        {
            ToEmail = recipientEmail,
            ToName = recipientName,
            Subject = $"{documentTypeLabel} - {document.SriAuthorizationNumber ?? document.Id.ToString()}",
            HtmlBody = HtmlEmailTemplate.Build(document),
            Attachments = attachments,
            Configuration = config
        };

        try
        {
            await RetryPipeline.ExecuteAsync(async ct =>
                await provider.SendAsync(emailMessage, ct), cancellationToken);

            logger.LogInformation("Email sent successfully for document {DocumentId} to {Recipient}",
                document.Id, recipientEmail);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email for document {DocumentId} to {Recipient} after retries",
                document.Id, recipientEmail);

            return false;
        }
    }

    public async Task<bool> TestConnectionAsync(
        EmailConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Para las pruebas siempre usamos el proveedor custom porque estamos probando una configuración específica
            await customEmailProvider.TestConnectionAsync(configuration, cancellationToken);
            logger.LogInformation("SMTP connection test succeeded for host {SmtpHost}", configuration.SmtpHost);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP connection test failed for host {SmtpHost}", configuration.SmtpHost);
            return false;
        }
    }
}
