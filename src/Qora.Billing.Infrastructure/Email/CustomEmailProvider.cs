using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Proveedor de email SMTP que usa las credenciales SMTP personalizadas propias del tenant.
/// La configuración se toma de la propiedad EmailMessage.Configuration al momento del envío.
/// </summary>
public class CustomEmailProvider(ILogger<CustomEmailProvider> logger) : IEmailProvider
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Configuration is null)
            throw new InvalidOperationException("La configuración del mensaje de correo debe estar definida para usar CustomEmailProvider.");

        logger.LogInformation("Sending email via custom SMTP ({Host}:{Port}) to {Recipient}",
            message.Configuration.SmtpHost, message.Configuration.SmtpPort, message.ToEmail);

        await SendWithConfigAsync(message, message.Configuration, cancellationToken);
    }

    public async Task TestConnectionAsync(EmailConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var testMessage = new EmailMessage
        {
            ToEmail = configuration.SenderEmail,
            ToName = configuration.SenderName,
            Subject = "Prueba de conexión SMTP - Qora Billing",
            HtmlBody = "<p>Configuración SMTP personalizada verificada correctamente.</p>",
            Configuration = configuration
        };

        await SendWithConfigAsync(testMessage, configuration, cancellationToken);
    }

    private static async Task SendWithConfigAsync(
        EmailMessage message,
        EmailConfiguration config,
        CancellationToken ct)
    {
        var mime = BuildMimeMessage(message, config);

        using var client = new SmtpClient();
        var secureSocket = config.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, secureSocket, ct);
        await client.AuthenticateAsync(config.SmtpUser, config.SmtpPassword, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static MimeMessage BuildMimeMessage(EmailMessage message, EmailConfiguration config)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(config.SenderName, config.SenderEmail));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToEmail));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder { HtmlBody = message.HtmlBody };
        foreach (var att in message.Attachments)
        {
            builder.Attachments.Add(att.FileName, att.Content, ContentType.Parse(att.ContentType));
        }

        mime.Body = builder.ToMessageBody();
        return mime;
    }
}
