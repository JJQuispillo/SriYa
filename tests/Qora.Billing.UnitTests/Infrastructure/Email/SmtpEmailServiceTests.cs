using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Email;

namespace Qora.Billing.UnitTests.Infrastructure.Email;

public class SmtpEmailServiceTests
{
    private readonly QoraEmailProvider _qoraProvider;
    private readonly CustomEmailProvider _customProvider;
    private readonly Mock<IRideGenerator> _rideGenerator;
    private readonly IOptions<QoraEmailSettings> _settings;
    private readonly SmtpEmailService _service;

    public SmtpEmailServiceTests()
    {
        var mockQoraOptions = Options.Create(new QoraEmailSettings
        {
            SmtpHost = "smtp.test.com",
            SmtpPort = 587,
            SmtpUser = "test@test.com",
            SmtpPassword = "password",
            UseSsl = true,
            SenderEmail = "noreply@test.com",
            SenderName = "Test"
        });

        _qoraProvider = new QoraEmailProvider(mockQoraOptions, NullLogger<QoraEmailProvider>.Instance);
        _customProvider = new CustomEmailProvider(NullLogger<CustomEmailProvider>.Instance);
        _rideGenerator = new Mock<IRideGenerator>();
        _settings = mockQoraOptions;

        _service = new SmtpEmailService(
            _qoraProvider,
            _customProvider,
            _rideGenerator.Object,
            _settings,
            NullLogger<SmtpEmailService>.Instance);
    }

    [Fact]
    public async Task SendDocumentEmailAsync_WhenEmailDisabled_ReturnsFalse()
    {
        // Arrange
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        // Email is disabled by default

        var document = Document.Create(
            tenant.Id,
            DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1792268071001" },
            new Dictionary<string, string> { ["correo"] = "buyer@test.com" });

        // Act
        var result = await _service.SendDocumentEmailAsync(document, tenant, CancellationToken.None);

        // Assert
        result.Should().BeFalse("email delivery is disabled by default for new tenants");
    }

    [Fact]
    public async Task SendDocumentEmailAsync_WhenNoBuyerEmail_ReturnsFalse()
    {
        // Arrange
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.ConfigureEmail(emailEnabled: true, emailProvider: EmailProvider.Qora);

        var document = Document.Create(
            tenant.Id,
            DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1792268071001" },
            new Dictionary<string, string> { ["razonSocialComprador"] = "Buyer Corp" }
            // Note: no "correo" key
        );

        // Act
        var result = await _service.SendDocumentEmailAsync(document, tenant, CancellationToken.None);

        // Assert
        result.Should().BeFalse("no recipient email is available in BuyerInfo");
    }
}
