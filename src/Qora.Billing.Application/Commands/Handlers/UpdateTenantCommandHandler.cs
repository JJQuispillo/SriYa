using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class UpdateTenantCommandHandler : IRequestHandler<UpdateTenantCommand, TenantResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateTenantCommandHandler(ITenantRepository tenantRepository, IUnitOfWork unitOfWork)
    {
        _tenantRepository = tenantRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<TenantResponse> Handle(UpdateTenantCommand command, CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} not found.");

        tenant.Update(command.Request.BusinessName, command.Request.TradeName);

        if (command.Request.IsActive.HasValue)
        {
            if (command.Request.IsActive.Value)
                tenant.Activate();
            else
                tenant.Deactivate();
        }

        await _tenantRepository.UpdateAsync(tenant, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new TenantResponse(
            tenant.Id,
            tenant.Ruc.Value,
            tenant.BusinessName,
            tenant.TradeName,
            tenant.IsActive,
            tenant.CreatedAt);
    }
}
