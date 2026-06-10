using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

/// <summary>
/// Delegador delgado: traduce el comando a la entrada del servicio de onboarding y delega la
/// composición atómica (tenant → certificado → API key, una transacción, rollback total) a
/// <see cref="ITenantBootstrapService"/> en la capa de Infrastructure.
/// </summary>
public class BootstrapTenantCommandHandler : IRequestHandler<BootstrapTenantCommand, BootstrapTenantResponse>
{
    private readonly ITenantBootstrapService _bootstrapService;

    public BootstrapTenantCommandHandler(ITenantBootstrapService bootstrapService)
    {
        _bootstrapService = bootstrapService;
    }

    public Task<BootstrapTenantResponse> Handle(BootstrapTenantCommand command, CancellationToken cancellationToken)
    {
        var input = new BootstrapTenantInput(
            command.Ruc,
            command.RazonSocial,
            command.NombreComercial,
            command.CorreoContacto,
            command.CertificateData,
            command.CertificatePassword,
            command.OwnerName,
            command.ApiKeyName);

        return _bootstrapService.BootstrapAsync(input, cancellationToken);
    }
}
