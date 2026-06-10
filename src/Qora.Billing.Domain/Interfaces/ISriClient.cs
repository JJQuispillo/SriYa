namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Cliente para los servicios web SOAP del SRI (Servicio de Rentas Internas).
/// </summary>
public interface ISriClient
{
    /// <summary>
    /// Envía un documento XML firmado al SRI para la validación de recepción.
    /// Devuelve el estado de la respuesta y los mensajes que haya.
    /// </summary>
    Task<SriSendResult> SendDocumentAsync(string signedXml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consulta el estado de autorización de un documento por su clave de acceso.
    /// </summary>
    Task<SriAuthorizationResult> CheckAuthorizationAsync(string accessKey, CancellationToken cancellationToken = default);
}

public record SriSendResult(bool IsAccepted, string Status, IReadOnlyList<string> Messages);

public record SriAuthorizationResult(
    bool IsAuthorized,
    string? AuthorizationNumber,
    DateTime? AuthorizationDate,
    string Status,
    IReadOnlyList<string> Messages);
