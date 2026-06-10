using MediatR;
using Qora.Billing.Application.Interfaces;

namespace Qora.Billing.Application.Commands;

/// <summary>
/// PL-2: exporta (siempre) y luego borra los datos del emisor <paramref name="TenantId"/> según la
/// política de retención. El ZIP de la exportación previa se escribe en <paramref name="ExportOutput"/>.
/// </summary>
public record DeleteTenantDataCommand(Guid TenantId, Stream ExportOutput) : IRequest<ScopedDeleteResult>;
