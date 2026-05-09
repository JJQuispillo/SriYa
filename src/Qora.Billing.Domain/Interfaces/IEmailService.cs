using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Sends email notifications for billing documents.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends the authorized document to the buyer's email address.
    /// Returns true if sent successfully, false if email is disabled or recipient is missing.
    /// </summary>
    Task<bool> SendDocumentEmailAsync(Document document, Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the SMTP connection using the provided configuration.
    /// </summary>
    Task<bool> TestConnectionAsync(ValueObjects.EmailConfiguration configuration, CancellationToken cancellationToken = default);
}
