using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetUsageQuery(Guid TenantId, string? Period = null) : IRequest<UsageResponse>;
