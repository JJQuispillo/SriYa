using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Onboarding atómico de un emisor (OB-1): crea el tenant, sube + valida el certificado .p12/.pfx
/// (validación ANTES de persistir) y emite una API key inicial — todo en UNA sola transacción con
/// rollback total ante cualquier fallo (RUC inválido, certificado/contraseña inválidos, RUC duplicado…):
/// no queda ningún tenant, certificado ni api_key a medio crear.
///
/// Lo implementa la capa de Infrastructure sobre la conexión PRIVILEGIADA (BYPASSRLS), porque cuando
/// arranca el bootstrap el tenant aún NO existe y no hay GUC app.current_tenant fijada: bajo el rol
/// billing_app (FORCE RLS, fail-closed) los INSERT serían rechazados. La conexión privilegiada hace que
/// el acceso "antes-de-que-exista-el-tenant" sea una decisión intencional y auditable (D3/D6), y cada
/// fila se inserta con su tenant_id correcto, de modo que los OTROS tenants siguen aislados por RLS.
/// </summary>
public interface ITenantBootstrapService
{
    Task<BootstrapTenantResponse> BootstrapAsync(
        BootstrapTenantInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Entrada del onboarding atómico. <paramref name="CertificateData"/> son los bytes del .p12/.pfx;
/// <paramref name="CertificatePassword"/> su contraseña; ambos se validan antes de cualquier persistencia.
/// </summary>
public record BootstrapTenantInput(
    string Ruc,
    string RazonSocial,
    string? NombreComercial,
    string? CorreoContacto,
    byte[] CertificateData,
    string CertificatePassword,
    string OwnerName,
    string ApiKeyName);
