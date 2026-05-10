using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record RegisterTenantCommand(string Ruc, string BusinessName, string? TradeName, string ContactEmail) : IRequest<RegisterTenantResponse>;
