using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetCertificatesQueryHandler : IRequestHandler<GetCertificatesQuery, List<CertificateResponse>>
{
    private readonly IElectronicSignatureRepository _signatureRepository;

    public GetCertificatesQueryHandler(IElectronicSignatureRepository signatureRepository)
    {
        _signatureRepository = signatureRepository;
    }

    public async Task<List<CertificateResponse>> Handle(
        GetCertificatesQuery query, CancellationToken cancellationToken)
    {
        // The repository currently only has GetActiveByTenantIdAsync.
        // For now, return the active one if it exists.
        var active = await _signatureRepository.GetActiveByTenantIdAsync(query.TenantId, cancellationToken);
        if (active is null)
            return [];

        return
        [
            new CertificateResponse(
                active.Id,
                active.OwnerName,
                active.ExpiresAt,
                active.IsActive,
                active.CreatedAt)
        ];
    }
}
