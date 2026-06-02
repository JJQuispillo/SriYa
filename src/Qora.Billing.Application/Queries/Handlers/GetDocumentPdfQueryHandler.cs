using MediatR;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetDocumentPdfQueryHandler : IRequestHandler<GetDocumentPdfQuery, byte[]?>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRideGenerator _rideGenerator;

    public GetDocumentPdfQueryHandler(
        IDocumentRepository documentRepository,
        IRideGenerator rideGenerator)
    {
        _documentRepository = documentRepository;
        _rideGenerator = rideGenerator;
    }

    public async Task<byte[]?> Handle(GetDocumentPdfQuery query, CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetByIdAsync(query.DocumentId, cancellationToken);
        if (document is null || document.TenantId != query.TenantId)
            return null;

        // Solo los documentos autorizados pueden generar PDFs del RIDE
        if (document.Status != DocumentStatus.Authorized)
            return null;

        return await _rideGenerator.GeneratePdfAsync(document, cancellationToken);
    }
}
