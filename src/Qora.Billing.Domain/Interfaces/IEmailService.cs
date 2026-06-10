using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Envía notificaciones por email para documentos de facturación.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Envía el documento autorizado a la dirección de email del comprador.
    /// Devuelve true si se envió correctamente, false si el email está deshabilitado o falta el destinatario.
    /// </summary>
    Task<bool> SendDocumentEmailAsync(Document document, Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prueba la conexión SMTP usando la configuración proporcionada.
    /// </summary>
    Task<bool> TestConnectionAsync(ValueObjects.EmailConfiguration configuration, CancellationToken cancellationToken = default);
}
