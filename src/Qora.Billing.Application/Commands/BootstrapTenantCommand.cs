using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

/// <summary>
/// Onboarding atómico de un emisor (OB-1): tenant + certificado + API key inicial en una sola
/// transacción con rollback total ante cualquier fallo. La clave de API se devuelve EN CLARO una sola vez.
/// </summary>
public record BootstrapTenantCommand(
    string Ruc,
    string RazonSocial,
    string? NombreComercial,
    string? CorreoContacto,
    byte[] CertificateData,
    string CertificatePassword,
    string OwnerName,
    string ApiKeyName) : IRequest<BootstrapTenantResponse>;
