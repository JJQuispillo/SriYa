using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetSubscriptionQuery(Guid TenantId) : IRequest<SubscriptionDto>;
