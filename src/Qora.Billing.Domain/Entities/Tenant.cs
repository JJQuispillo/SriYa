using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Domain.Entities;

public class Tenant : BaseEntity
{
    public Ruc Ruc { get; private set; } = null!;
    public string BusinessName { get; private set; } = string.Empty;
    public string? TradeName { get; private set; }
    public string? ContactEmail { get; private set; }
    public bool IsActive { get; private set; }

    // ── Configuración de envío de email ──────────────────────────────────────
    public bool EmailEnabled { get; private set; } = false;
    public EmailProvider EmailProvider { get; private set; } = EmailProvider.Qora;
    public string? SmtpHost { get; private set; }
    public int? SmtpPort { get; private set; }
    public string? SmtpUser { get; private set; }
    public string? SmtpPassword { get; private set; }
    public bool UseSsl { get; private set; } = true;
    public string? SenderEmail { get; private set; }
    public string? SenderName { get; private set; }

    // ── Modo de numeración del secuencial ────────────────────────────────────
    /// <summary>
    /// Selecciona el modo de numeración del secuencial del comprobante.
    /// <c>false</c> (default) = modo CLIENTE: el secuencial lo provee quien emite (monotonicidad MAX+1).
    /// <c>true</c> = modo AUTO: el servidor asigna MAX+1 server-side bajo lock. No requiere migración de
    /// datos: AUTO arranca leyendo el mismo MAX(secuencial) de los documentos existentes.
    /// </summary>
    public bool AutoGenerateSecuencial { get; private set; } = false;

    private Tenant() { } // EF Core

    public static Tenant Create(string ruc, string businessName, string? tradeName = null, string? contactEmail = null)
    {
        var tenant = new Tenant
        {
            Ruc = new Ruc(ruc),
            BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName)),
            TradeName = tradeName,
            ContactEmail = contactEmail,
            IsActive = true
        };

        return tenant;
    }

    public void Update(string businessName, string? tradeName)
    {
        BusinessName = businessName ?? throw new ArgumentNullException(nameof(businessName));
        TradeName = tradeName;
        SetUpdatedAt();
    }

    public void Deactivate()
    {
        IsActive = false;
        SetUpdatedAt();
    }

    /// <summary>
    /// Anonimiza el emisor para el borrado con retención fiscal (PL-2). RETIENE el RUC y la razón social
    /// (datos fiscales del emisor exigidos para la retención) y desactiva el tenant; REDACTA la PII y las
    /// credenciales: nombre comercial, correo de contacto y toda la configuración SMTP (host/usuario/
    /// contraseña/remitente). Idempotente.
    /// </summary>
    public void Anonymize()
    {
        TradeName = null;
        ContactEmail = null;
        EmailEnabled = false;
        SmtpHost = null;
        SmtpPort = null;
        SmtpUser = null;
        SmtpPassword = null;
        SenderEmail = null;
        SenderName = null;
        IsActive = false;
        SetUpdatedAt();
    }

    public void Activate()
    {
        IsActive = true;
        SetUpdatedAt();
    }

    public void EnsureActive()
    {
        if (!IsActive)
            throw new TenantInactiveException(Id);
    }

    /// <summary>
    /// Actualiza la configuración de envío de email del tenant.
    /// Pase null en los campos SMTP para dejarlos sin cambios cuando solo se alterna enabled/provider.
    /// </summary>
    public void ConfigureEmail(
        bool emailEnabled,
        EmailProvider emailProvider,
        string? smtpHost = null,
        int? smtpPort = null,
        string? smtpUser = null,
        string? smtpPassword = null,
        bool useSsl = true,
        string? senderEmail = null,
        string? senderName = null)
    {
        EmailEnabled = emailEnabled;
        EmailProvider = emailProvider;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpUser = smtpUser;
        if (smtpPassword != null) SmtpPassword = smtpPassword;
        UseSsl = useSsl;
        SenderEmail = senderEmail;
        SenderName = senderName;
        SetUpdatedAt();
    }

    /// <summary>
    /// Configura el modo de numeración del secuencial del tenant.
    /// <c>true</c> = AUTO (el servidor asigna MAX+1); <c>false</c> = CLIENTE (lo provee el emisor).
    /// </summary>
    public void ConfigureSecuencialMode(bool autoGenerateSecuencial)
    {
        AutoGenerateSecuencial = autoGenerateSecuencial;
        SetUpdatedAt();
    }
}
