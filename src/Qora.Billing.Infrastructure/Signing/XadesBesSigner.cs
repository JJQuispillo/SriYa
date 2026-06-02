using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Signing;

/// <summary>
/// Firma documentos XML usando el formato XAdES-BES con certificados PKCS#12 (.p12).
/// Usa RSA-SHA1 según lo requiere el SRI y canonicalización C14N.
/// SEGURIDAD: Nunca registra en logs los datos del certificado ni las contraseñas.
/// </summary>
public class XadesBesSigner : IDocumentSigner
{
    private const string XadesNamespace = "http://uri.etsi.org/01903/v1.3.2#";

    public Task<string> SignDocumentAsync(string xml, byte[] certificateData, string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(certificateData);
        ArgumentNullException.ThrowIfNull(password);

        if (certificateData.Length == 0)
            throw new ArgumentException("Los datos del certificado no pueden estar vacíos.", nameof(certificateData));

        var signedXml = SignXml(xml, certificateData, password);
        return Task.FromResult(signedXml);
    }

    private static string SignXml(string xml, byte[] certificateData, string password)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xml);

        using var certificate = X509CertificateLoader.LoadPkcs12(certificateData, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

        var rsaKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("El certificado no contiene una clave privada RSA.");

        var signedXml = new SignedXml(xmlDoc)
        {
            SigningKey = rsaKey
        };

        // Referencia al contenido del documento (firmar el documento completo)
        var reference = new Reference
        {
            Uri = "#comprobante",
            DigestMethod = SignedXml.XmlDsigSHA1Url
        };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signedXml.AddReference(reference);

        // Método de firma: RSA-SHA1 (requerido por el SRI)
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;

        // Canonicalización C14N
        signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;

        // Key info con el certificado
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;

        // Calcular primero la firma (solo la referencia al documento)
        signedXml.ComputeSignature();

        // Obtener el elemento XML Signature
        var signatureElement = signedXml.GetXml();

        // Ahora añadir XAdES QualifyingProperties como un Object dentro del Signature
        var xadesObject = BuildXadesQualifyingProperties(xmlDoc, certificate);
        var dsNs = SignedXml.XmlDsigNamespaceUrl;
        var objectElement = xmlDoc.CreateElement("ds", "Object", dsNs);
        objectElement.AppendChild(xmlDoc.ImportNode(xadesObject, true));

        // Importar la firma al documento y añadir el objeto XAdES
        var importedSignature = xmlDoc.ImportNode(signatureElement, true);
        importedSignature.AppendChild(objectElement);
        xmlDoc.DocumentElement!.AppendChild(importedSignature);

        // Devolver el XML firmado como string UTF-8 sin BOM
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false
        };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            xmlDoc.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XmlElement BuildXadesQualifyingProperties(XmlDocument ownerDoc, X509Certificate2 certificate)
    {
        var xadesNs = XadesNamespace;

        var qpElement = ownerDoc.CreateElement("etsi", "QualifyingProperties", xadesNs);
        qpElement.SetAttribute("Target", "#Signature");

        var signedProperties = ownerDoc.CreateElement("etsi", "SignedProperties", xadesNs);
        signedProperties.SetAttribute("Id", $"SignedProperties-{Guid.NewGuid():N}");

        // Propiedades SignedSignatureProperties
        var signedSigProps = ownerDoc.CreateElement("etsi", "SignedSignatureProperties", xadesNs);

        // SigningTime (hora de firma)
        var signingTime = ownerDoc.CreateElement("etsi", "SigningTime", xadesNs);
        signingTime.InnerText = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        signedSigProps.AppendChild(signingTime);

        // SigningCertificate (certificado de firma)
        var signingCert = ownerDoc.CreateElement("etsi", "SigningCertificate", xadesNs);
        var cert = ownerDoc.CreateElement("etsi", "Cert", xadesNs);

        var certDigest = ownerDoc.CreateElement("etsi", "CertDigest", xadesNs);

        var digestMethod = ownerDoc.CreateElement("ds", "DigestMethod", SignedXml.XmlDsigNamespaceUrl);
        digestMethod.SetAttribute("Algorithm", SignedXml.XmlDsigSHA1Url);
        certDigest.AppendChild(digestMethod);

        var digestValue = ownerDoc.CreateElement("ds", "DigestValue", SignedXml.XmlDsigNamespaceUrl);
        var certHash = SHA1.HashData(certificate.RawData);
        digestValue.InnerText = Convert.ToBase64String(certHash);
        certDigest.AppendChild(digestValue);
        cert.AppendChild(certDigest);

        var issuerSerial = ownerDoc.CreateElement("etsi", "IssuerSerial", xadesNs);
        var issuerName = ownerDoc.CreateElement("ds", "X509IssuerName", SignedXml.XmlDsigNamespaceUrl);
        issuerName.InnerText = certificate.Issuer;
        issuerSerial.AppendChild(issuerName);

        var serialNumber = ownerDoc.CreateElement("ds", "X509SerialNumber", SignedXml.XmlDsigNamespaceUrl);
        serialNumber.InnerText = certificate.SerialNumber;
        issuerSerial.AppendChild(serialNumber);
        cert.AppendChild(issuerSerial);

        signingCert.AppendChild(cert);
        signedSigProps.AppendChild(signingCert);

        signedProperties.AppendChild(signedSigProps);

        // Propiedades SignedDataObjectProperties
        var signedDataObjProps = ownerDoc.CreateElement("etsi", "SignedDataObjectProperties", xadesNs);

        var dataObjectFormat = ownerDoc.CreateElement("etsi", "DataObjectFormat", xadesNs);
        dataObjectFormat.SetAttribute("ObjectReference", "#comprobante");

        var description = ownerDoc.CreateElement("etsi", "Description", xadesNs);
        description.InnerText = "contenido comprobante";
        dataObjectFormat.AppendChild(description);

        var mimeType = ownerDoc.CreateElement("etsi", "MimeType", xadesNs);
        mimeType.InnerText = "text/xml";
        dataObjectFormat.AppendChild(mimeType);

        signedDataObjProps.AppendChild(dataObjectFormat);
        signedProperties.AppendChild(signedDataObjProps);

        qpElement.AppendChild(signedProperties);

        return qpElement;
    }
}
