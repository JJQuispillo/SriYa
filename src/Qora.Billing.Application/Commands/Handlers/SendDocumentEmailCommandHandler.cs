using MediatR;
using Microsoft.Extensions.Logging;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class SendDocumentEmailCommandHandler(
    IDocumentRepository documentRepository,
    ITenantRepository tenantRepository,
    IEmailService emailService,
    ILogger<SendDocumentEmailCommandHandler> logger) : IRequestHandler<SendDocumentEmailCommand, bool>
{
    public async Task<bool> Handle(SendDocumentEmailCommand command, CancellationToken cancellationToken)
    {
        var document = await documentRepository.GetByIdAsync(command.DocumentId, cancellationToken)
            ?? throw new BillingDomainException($"Documento {command.DocumentId} no encontrado.");

        if (document.TenantId != command.TenantId)
            throw new BillingDomainException($"El documento {command.DocumentId} no pertenece al tenant {command.TenantId}.");

        var tenant = await tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");

        var sent = await emailService.SendDocumentEmailAsync(document, tenant, cancellationToken);

        logger.LogInformation(
            "Email dispatch for document {DocumentId}: {Result}",
            command.DocumentId,
            sent ? "sent" : "skipped");

        return sent;
    }
}
