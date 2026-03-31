using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetCertificatesQuery(Guid TenantId) : IRequest<List<CertificateResponse>>;
