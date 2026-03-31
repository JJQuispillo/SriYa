using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetTenantByIdQueryHandler : IRequestHandler<GetTenantByIdQuery, TenantResponse?>
{
    private readonly ITenantRepository _tenantRepository;

    public GetTenantByIdQueryHandler(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    public async Task<TenantResponse?> Handle(GetTenantByIdQuery query, CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(query.TenantId, cancellationToken);
        if (tenant is null)
            return null;

        return new TenantResponse(
            tenant.Id,
            tenant.Ruc.Value,
            tenant.BusinessName,
            tenant.TradeName,
            tenant.IsActive,
            tenant.CreatedAt);
    }
}
