using MediatR;

namespace Qora.Billing.Application.Commands;

/// <summary>
/// PL-1: exporta todos los datos del emisor <paramref name="TenantId"/> como ZIP, escribiéndolo en
/// <paramref name="Output"/> en streaming. Devuelve el número de bytes escritos.
/// </summary>
public record ExportTenantCommand(Guid TenantId, Stream Output) : IRequest<long>;
