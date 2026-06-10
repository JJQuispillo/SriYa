namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Firma un documento XML usando XAdES-BES con un certificado PKCS#12.
/// </summary>
public interface IDocumentSigner
{
    Task<string> SignDocumentAsync(string xml, byte[] certificateData, string password,
        CancellationToken cancellationToken = default);
}
