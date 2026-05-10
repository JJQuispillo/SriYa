using MediatR;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class CreateCheckoutSessionCommandHandler : IRequestHandler<CreateCheckoutSessionCommand, string>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IPlanRepository _planRepository;
    private readonly IStripeService _stripeService;

    public CreateCheckoutSessionCommandHandler(
        ITenantRepository tenantRepository,
        IPlanRepository planRepository,
        IStripeService stripeService)
    {
        _tenantRepository = tenantRepository;
        _planRepository = planRepository;
        _stripeService = stripeService;
    }

    public async Task<string> Handle(CreateCheckoutSessionCommand command, CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");
        tenant.EnsureActive();

        if (string.IsNullOrWhiteSpace(tenant.ContactEmail))
            throw new BillingDomainException($"El tenant {command.TenantId} no tiene email de contacto configurado.");

        var plan = await _planRepository.GetByIdAsync(command.PlanId, cancellationToken)
            ?? throw new BillingDomainException($"Plan {command.PlanId} no encontrado.");

        if (!plan.IsActive)
            throw new BillingDomainException($"El plan '{plan.Name}' no está activo.");

        var checkoutUrl = await _stripeService.CreateCheckoutSessionAsync(
            tenant.Id,
            plan.Id,
            tenant.ContactEmail,
            cancellationToken);

        return checkoutUrl;
    }
}
