using MediatR;
using Microsoft.Extensions.Logging;
using Qora.Billing.Domain.Events;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.DomainEventHandlers;

/// <summary>
/// Envía automáticamente un correo al comprador cada vez que un documento es autorizado por el SRI.
/// Este handler es fire-and-forget: nunca lanza excepciones para no interrumpir el pipeline principal.
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
            // Nunca propagar — un fallo en el correo no debe afectar la autorización del documento
            logger.LogError(ex, "Failed to send auto-email for authorized document {DocumentId}.", notification.DocumentId);
        }
    }
}
