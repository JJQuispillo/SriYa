using System.Security.Cryptography.X509Certificates;
using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class UploadCertificateCommandHandler : IRequestHandler<UploadCertificateCommand, CertificateResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IElectronicSignatureRepository _signatureRepository;
    private readonly IUnitOfWork _unitOfWork;

    public UploadCertificateCommandHandler(
        ITenantRepository tenantRepository,
        IElectronicSignatureRepository signatureRepository,
        IUnitOfWork unitOfWork)
    {
        _tenantRepository = tenantRepository;
        _signatureRepository = signatureRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<CertificateResponse> Handle(UploadCertificateCommand command, CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");
        tenant.EnsureActive();

        // Extrae la fecha de expiración del certificado PKCS#12
        DateTime expiresAt;
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12(command.CertificateData, command.Password);
            expiresAt = cert.NotAfter.ToUniversalTime();
        }
        catch (Exception ex)
        {
            throw new BillingDomainException($"Certificado o contraseña inválidos: {ex.Message}", ex);
        }

        // Desactiva cualquier certificado activo existente para este tenant
        var existingCert = await _signatureRepository.GetActiveByTenantIdAsync(command.TenantId, cancellationToken);
        if (existingCert is not null)
        {
            existingCert.Deactivate();
            await _signatureRepository.UpdateAsync(existingCert, cancellationToken);
        }

        // Almacena los datos del certificado y la contraseña cifrada
        // En producción, la contraseña debería cifrarse con una clave de data protection antes de almacenarla
        var signature = ElectronicSignature.Create(
            command.TenantId,
            command.CertificateData,
            command.Password, // Debería cifrarse en producción
            command.OwnerName,
            expiresAt);

        await _signatureRepository.CreateAsync(signature, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CertificateResponse(
            signature.Id,
            signature.OwnerName,
            signature.ExpiresAt,
            signature.IsActive,
            signature.CreatedAt);
    }
}
