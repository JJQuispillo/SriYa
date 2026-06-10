using MediatR;
using Qora.Billing.Application.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

/// <summary>
/// PL-1: delega en <see cref="ITenantLifecycleService"/> para exportar los datos del emisor. La operación
/// es acotada al tenant en contexto (RLS): el ZIP sólo contiene los datos de ese emisor.
/// </summary>
public class ExportTenantCommandHandler : IRequestHandler<ExportTenantCommand, long>
{
    private readonly ITenantLifecycleService _lifecycleService;

    public ExportTenantCommandHandler(ITenantLifecycleService lifecycleService)
    {
        _lifecycleService = lifecycleService;
    }

    public Task<long> Handle(ExportTenantCommand command, CancellationToken cancellationToken) =>
        _lifecycleService.ExportTenantAsync(command.TenantId, command.Output, cancellationToken);
}
