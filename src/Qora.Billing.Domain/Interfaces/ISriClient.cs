namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Client for SRI (Servicio de Rentas Internas) SOAP web services.
/// </summary>
public interface ISriClient
{
    /// <summary>
    /// Sends a signed XML document to SRI for reception validation.
    /// Returns the response status and any messages.
    /// </summary>
    Task<SriSendResult> SendDocumentAsync(string signedXml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the authorization status of a document by its access key.
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
