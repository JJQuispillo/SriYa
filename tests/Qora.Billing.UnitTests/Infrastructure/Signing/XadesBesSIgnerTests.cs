using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FluentAssertions;
using Qora.Billing.Infrastructure.Signing;

namespace Qora.Billing.UnitTests.Infrastructure.Signing;

public class XadesBesSignerTests
{
    private readonly XadesBesSigner _signer = new();

    private static (byte[] CertData, string Password) CreateTestCertificate()
    {
        var password = "TestPassword123!";
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=Test Signer, O=Test Org, C=EC",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var pfxData = cert.Export(X509ContentType.Pfx, password);
        return (pfxData, password);
    }

    private static string CreateTestXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8"?>
            <factura id="comprobante" version="2.1.0">
                <infoTributaria>
                    <ambiente>1</ambiente>
                    <ruc>1792268071001</ruc>
                </infoTributaria>
                <infoFactura>
                    <fechaEmision>18/03/2026</fechaEmision>
                </infoFactura>
            </factura>
            """;
    }

    [Fact]
    public async Task SignDocumentAsync_ShouldProduceSignedXml()
    {
        var (certData, password) = CreateTestCertificate();
        var xml = CreateTestXml();

        var signedXml = await _signer.SignDocumentAsync(xml, certData, password);

        signedXml.Should().NotBeNullOrWhiteSpace();
        signedXml.Should().Contain("Signature");
    }

    [Fact]
    public async Task SignDocumentAsync_ShouldContainXadesSignedProperties()
    {
        var (certData, password) = CreateTestCertificate();
        var xml = CreateTestXml();

        var signedXml = await _signer.SignDocumentAsync(xml, certData, password);

        signedXml.Should().Contain("SignedProperties");
        signedXml.Should().Contain("SigningTime");
        signedXml.Should().Contain("SigningCertificate");
    }

    [Fact]
    public async Task SignDocumentAsync_ShouldContainKeyInfo()
    {
        var (certData, password) = CreateTestCertificate();
        var xml = CreateTestXml();

        var signedXml = await _signer.SignDocumentAsync(xml, certData, password);

        signedXml.Should().Contain("KeyInfo");
        signedXml.Should().Contain("X509Data");
    }

    [Fact]
    public async Task SignDocumentAsync_ShouldPreserveOriginalContent()
    {
        var (certData, password) = CreateTestCertificate();
        var xml = CreateTestXml();

        var signedXml = await _signer.SignDocumentAsync(xml, certData, password);

        signedXml.Should().Contain("infoTributaria");
        signedXml.Should().Contain("1792268071001");
        signedXml.Should().Contain("infoFactura");
    }

    [Fact]
    public async Task SignDocumentAsync_ShouldContainDataObjectFormat()
    {
        var (certData, password) = CreateTestCertificate();
        var xml = CreateTestXml();

        var signedXml = await _signer.SignDocumentAsync(xml, certData, password);

        signedXml.Should().Contain("DataObjectFormat");
        signedXml.Should().Contain("text/xml");
    }

    [Fact]
    public async Task SignDocumentAsync_WithNullXml_ShouldThrow()
    {
        var (certData, password) = CreateTestCertificate();

        var act = () => _signer.SignDocumentAsync(null!, certData, password);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SignDocumentAsync_WithNullCertData_ShouldThrow()
    {
        var xml = CreateTestXml();

        var act = () => _signer.SignDocumentAsync(xml, null!, "pass");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SignDocumentAsync_WithEmptyCertData_ShouldThrow()
    {
        var xml = CreateTestXml();

        var act = () => _signer.SignDocumentAsync(xml, [], "pass");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public async Task SignDocumentAsync_WithWrongPassword_ShouldThrow()
    {
        var (certData, _) = CreateTestCertificate();
        var xml = CreateTestXml();

        var act = () => _signer.SignDocumentAsync(xml, certData, "WrongPassword");

        await act.Should().ThrowAsync<CryptographicException>();
    }
}
