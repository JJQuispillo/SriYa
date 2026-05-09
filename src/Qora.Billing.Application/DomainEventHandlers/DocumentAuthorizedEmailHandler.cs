using MediatR;
using Microsoft.Extensions.Logging;
using Qora.Billing.Domain.Events;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.DomainEventHandlers;

/// <summary>
/// Automatically sends an email to the buyer whenever a document is authorized by SRI.
/// This handler is fire-and-forget: it never throws so as not to interrupt the main pipeline.
/// </summary>
public class DocumentAuthorizedEmailHandler(
    IDocumentRepository documentRepository,
    ITenantRepository tenantRepository,
    IEmailService emailService,
    ILogger<DocumentAuthorizedEmailHandler> logger) : INotificationHandler<DocumentAuthorizedEvent>
{
    public async Task Handle(DocumentAuthorizedEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            var document = await documentRepository.GetByIdAsync(notification.DocumentId, cancellationToken);
            if (document is null)
            {
                logger.LogWarning("DocumentAuthorizedEmailHandler: Document {DocumentId} not found. Skipping email.", notification.DocumentId);
                return;
            }

            var tenant = await tenantRepository.GetByIdAsync(document.TenantId, cancellationToken);
            if (tenant is null)
            {
                logger.LogWarning("DocumentAuthorizedEmailHandler: Tenant {TenantId} not found for document {DocumentId}. Skipping email.", document.TenantId, notification.DocumentId);
                return;
            }

            var sent = await emailService.SendDocumentEmailAsync(document, tenant, cancellationToken);

            if (sent)
                logger.LogInformation("Auto-email sent for authorized document {DocumentId} (tenant {TenantId}).", notification.DocumentId, tenant.Id);
            else
                logger.LogDebug("Auto-email skipped for document {DocumentId} (email disabled or no recipient).", notification.DocumentId);
        }
        catch (Exception ex)
        {
            // Never propagate — email failure must not affect document authorization
            logger.LogError(ex, "Failed to send auto-email for authorized document {DocumentId}.", notification.DocumentId);
        }
    }
}
