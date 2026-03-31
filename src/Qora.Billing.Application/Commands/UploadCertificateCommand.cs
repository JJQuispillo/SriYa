using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record UploadCertificateCommand(
    Guid TenantId,
    byte[] CertificateData,
    string Password,
    string OwnerName) : IRequest<CertificateResponse>;
