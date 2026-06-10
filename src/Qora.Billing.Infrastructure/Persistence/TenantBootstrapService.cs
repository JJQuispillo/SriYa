using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// Implementación del onboarding atómico de un emisor (OB-1).
///
/// Compone las tres operaciones existentes —crear tenant, subir+validar certificado, emitir API key— en
/// UNA sola transacción explícita con rollback total: si algo falla (RUC inválido/duplicado, certificado o
/// contraseña inválidos…) no queda persistido NADA (ni tenant, ni firma, ni api_key).
///
/// Corre sobre la conexión PRIVILEGIADA (billing_privileged, BYPASSRLS): cuando arranca el bootstrap el
/// tenant aún no existe y no hay GUC app.current_tenant fijada, de modo que bajo billing_app (FORCE RLS,
/// fail-closed) los INSERT serían rechazados por las políticas RLS. El acceso "antes-de-que-exista-el-
/// tenant" es por tanto una decisión intencional y auditable (D3/D6), no un estado accidental de la GUC.
/// Cada fila se inserta con su tenant_id correcto, así que el aislamiento de los OTROS tenants por RLS se
/// mantiene intacto.
///
/// Orden de validación: el certificado .p12/.pfx se valida (parseo + contraseña) ANTES de persistir
/// cualquier fila — un certificado/contraseña inválidos lanzan antes de tocar la base de datos.
/// </summary>
public sealed class TenantBootstrapService : ITenantBootstrapService
{
    private readonly BillingPrivilegedDbContext _context;
    private readonly ApiKeySettings _apiKeySettings;
    private readonly ILogger<TenantBootstrapService> _logger;

    public TenantBootstrapService(
        BillingPrivilegedDbContext context,
        IOptions<ApiKeySettings> apiKeySettings,
        ILogger<TenantBootstrapService> logger)
    {
        _context = context;
        _apiKeySettings = apiKeySettings.Value;
        _logger = logger;
    }

    public async Task<BootstrapTenantResponse> BootstrapAsync(
        BootstrapTenantInput input, CancellationToken cancellationToken = default)
    {
        // 1. Validar el certificado ANTES de persistir nada. Si el certificado o la contraseña son
        //    inválidos, lanzamos aquí — no se abre transacción ni se inserta fila alguna.
        var expiresAt = ValidateCertificate(input.CertificateData, input.CertificatePassword);

        // 2. Una sola transacción para las tres inserciones. Cualquier excepción => rollback total.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 2a. RUC duplicado → rechazo (BillingDomainException → 400), dentro de la transacción para
            //     que el rollback sea uniforme aunque ya se hubiera abierto. Construir el value object Ruc
            //     valida el formato (InvalidRucException → 400 si es inválido) y permite que el value
            //     converter de EF traduzca la comparación.
            var rucVo = new Ruc(input.Ruc);
            var rucExists = await _context.Tenants.AnyAsync(t => t.Ruc == rucVo, cancellationToken);
            if (rucExists)
                throw new BillingDomainException($"Ya existe un tenant con el RUC '{input.Ruc}'.");

            // 2b. Crear el emisor.
            var tenant = Tenant.Create(input.Ruc, input.RazonSocial, input.NombreComercial, input.CorreoContacto);
            await _context.Tenants.AddAsync(tenant, cancellationToken);

            // 2c. Firma electrónica (certificado validado arriba). La contraseña debería cifrarse en
            //     producción (mismo TODO que UploadCertificate); aquí se conserva la semántica existente.
            var signature = ElectronicSignature.Create(
                tenant.Id,
                input.CertificateData,
                input.CertificatePassword,
                input.OwnerName,
                expiresAt);
            await _context.ElectronicSignatures.AddAsync(signature, cancellationToken);

            // 2d. API key inicial. Misma mecánica que CreateApiKeyCommandHandler: clave aleatoria con
            //     prefijo por entorno, hash SHA-256 almacenado, texto plano devuelto SOLO una vez.
            var plaintextKey = GenerateApiKey();
            var keyHash = CreateApiKeyCommandHandler.HashApiKey(plaintextKey);
            var apiKey = ApiKey.Create(tenant.Id, keyHash, input.ApiKeyName);
            await _context.ApiKeys.AddAsync(apiKey, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Onboarding del emisor {TenantId} (RUC {Ruc}): tenant + certificado + API key creados atómicamente.",
                tenant.Id, input.Ruc);

            return new BootstrapTenantResponse(
                tenant.Id,
                tenant.Ruc.Value,
                tenant.BusinessName,
                signature.Id,
                signature.ExpiresAt,
                apiKey.Id,
                plaintextKey,
                tenant.CreatedAt);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Valida el certificado PKCS#12 (parseo + contraseña) y devuelve su fecha de expiración en UTC.
    /// Un certificado o contraseña inválidos lanzan <see cref="BillingDomainException"/> (→ 400),
    /// misma semántica que UploadCertificateCommandHandler.
    /// </summary>
    private static DateTime ValidateCertificate(byte[] certificateData, string password)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12(certificateData, password);
            return cert.NotAfter.ToUniversalTime();
        }
        catch (Exception ex)
        {
            throw new BillingDomainException($"Certificado o contraseña inválidos: {ex.Message}", ex);
        }
    }

    private string GenerateApiKey()
    {
        var prefix = _apiKeySettings.Prefix;
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var randomPart = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        return $"{prefix}{randomPart}";
    }
}
