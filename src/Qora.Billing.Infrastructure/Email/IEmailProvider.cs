using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Abstracción de bajo nivel para el envío de email por SMTP.
/// Las implementaciones eligen su fuente de credenciales (plataforma Qora vs. configuración personalizada del tenant).
/// </summary>
public interface IEmailProvider
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    Task TestConnectionAsync(EmailConfiguration configuration, CancellationToken cancellationToken = default);
}
