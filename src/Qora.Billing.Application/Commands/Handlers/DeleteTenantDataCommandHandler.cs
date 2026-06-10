using MediatR;
using Qora.Billing.Application.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

/// <summary>
/// PL-2: delega en <see cref="ITenantLifecycleService"/> el borrado con retención. El servicio exporta
/// SIEMPRE antes de tocar cualquier fila (export-always). Acotado al tenant en contexto (RLS).
/// </summary>
public class DeleteTenantDataCommandHandler : IRequestHandler<DeleteTenantDataCommand, ScopedDeleteResult>
{
    private readonly ITenantLifecycleService _lifecycleService;

    public DeleteTenantDataCommandHandler(ITenantLifecycleService lifecycleService)
    {
        _lifecycleService = lifecycleService;
    }

    public Task<ScopedDeleteResult> Handle(DeleteTenantDataCommand command, CancellationToken cancellationToken) =>
        _lifecycleService.DeleteTenantDataAsync(command.TenantId, command.ExportOutput, cancellationToken);
}
