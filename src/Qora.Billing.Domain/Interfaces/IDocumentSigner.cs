namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Signs an XML document using XAdES-BES with a PKCS#12 certificate.
/// </summary>
public interface IDocumentSigner
{
    Task<string> SignDocumentAsync(string xml, byte[] certificateData, string password,
        CancellationToken cancellationToken = default);
}
