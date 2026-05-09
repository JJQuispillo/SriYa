using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Low-level SMTP email delivery abstraction.
/// Implementations choose their credential source (Qora platform vs. custom tenant config).
/// </summary>
public interface IEmailProvider
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    Task TestConnectionAsync(EmailConfiguration configuration, CancellationToken cancellationToken = default);
}
