using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class CreateTenantCommandHandler : IRequestHandler<CreateTenantCommand, TenantResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IUnitOfWork _unitOfWork;

    public CreateTenantCommandHandler(ITenantRepository tenantRepository, IUnitOfWork unitOfWork)
    {
        _tenantRepository = tenantRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<TenantResponse> Handle(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        // Verifica si hay un RUC duplicado
        var existing = await _tenantRepository.GetByRucAsync(command.Request.Ruc, cancellationToken);
        if (existing is not null)
            throw new BillingDomainException($"Ya existe un tenant con el RUC '{command.Request.Ruc}'.");

        var tenant = Tenant.Create(command.Request.Ruc, command.Request.BusinessName, command.Request.TradeName);
        await _tenantRepository.CreateAsync(tenant, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return MapToResponse(tenant);
    }

    private static TenantResponse MapToResponse(Tenant tenant)
    {
        return new TenantResponse(
            tenant.Id,
            tenant.Ruc.Value,
            tenant.BusinessName,
            tenant.TradeName,
            tenant.IsActive,
            tenant.CreatedAt);
    }
}
