using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;
using DomainDocumentType = Qora.Billing.Domain.Enums.DocumentType;
using SriTaxCode = Qora.Billing.Domain.Entities.SriTaxCode;

namespace Qora.Billing.UnitTests.Application.Commands;

public class ProcessDocumentCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IDocumentRepository> _documentRepo = new();
    private readonly Mock<IElectronicSignatureRepository> _signatureRepo = new();
    private readonly Mock<IDocumentEventRepository> _eventRepo = new();
    private readonly Mock<ISriTaxCodeRepository> _sriTaxCodeRepo = new();
    private readonly Mock<IDocumentTypeStrategy> _strategy = new();
    private readonly Mock<IDocumentSigner> _signer = new();
    private readonly Mock<ISriClient> _sriClient = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<ProcessDocumentCommandHandler>> _logger = new();

    public ProcessDocumentCommandHandlerTests()
    {
        _strategy.Setup(s => s.DocumentType).Returns(DomainDocumentType.Factura);

        // Default: return a valid SriTaxCode for IVA 15% (TaxCode="2", PercentageCode="4")
        _sriTaxCodeRepo
            .Setup(r => r.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SriTaxCode.Create("2", "4", 15m, "IVA 15%"));
    }

    private ProcessDocumentCommandHandler CreateHandler() => new(
        _tenantRepo.Object, _documentRepo.Object, _signatureRepo.Object,
        _eventRepo.Object, _sriTaxCodeRepo.Object,
        new[] { _strategy.Object },
        _signer.Object, _sriClient.Object, _unitOfWork.Object,
        _logger.Object);

    private static Tenant CreateActiveTenant()
    {
        return Tenant.Create("1792268071001", "Test Corp");
    }

    private static ElectronicSignature CreateValidSignature(Guid tenantId)
    {
        return ElectronicSignature.Create(
            tenantId, [0x01, 0x02], "password", "Owner", DateTime.UtcNow.AddYears(1));
    }

    private static string GenerateTestXml()
    {
        var baseDigits = "180320260117922680710011001001000000012372816811";
        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = baseDigits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (baseDigits.Length - 1 - i) % weights.Length;
            sum += (baseDigits[i] - '0') * weights[weightIndex];
        }
        var remainder = sum % 11;
        var checkDigit = 11 - remainder;
        checkDigit = checkDigit switch { 11 => 0, 10 => 1, _ => checkDigit };
        var accessKey = baseDigits + checkDigit;
        return $"<factura><infoTributaria><claveAcceso>{accessKey}</claveAcceso></infoTributaria></factura>";
    }

    private ProcessDocumentCommand CreateValidCommand(Guid tenantId)
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Test Corp" },
                { "dirMatriz", "Quito" },
                { "estab", "001" },
                { "ptoEmi", "001" },
                { "secuencial", "000000001" }
            },
            new Dictionary<string, string>
            {
                { "identificacion", "0102030405001" },
                { "razonSocial", "Buyer Corp" },
                { "tipoIdentificacion", "04" }
            },
            [
                new DocumentItemDto("PROD001", "Test Product", 2, 50.00m, 0, 15m, "2", "4")
            ]);

        return new ProcessDocumentCommand(tenantId, request);
    }

    [Fact]
    public async Task Handle_WhenTenantNotFound_ShouldThrowBillingDomainException()
    {
        var tenantId = Guid.NewGuid();
        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenantId);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<BillingDomainException>();
    }

    [Fact]
    public async Task Handle_WhenNoCertificate_ShouldThrowBillingDomainException()
    {
        var tenant = CreateActiveTenant();
        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElectronicSignature?)null);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<BillingDomainException>();
    }

    [Fact]
    public async Task Handle_WhenValidationFails_ShouldThrowDocumentValidationException()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Missing field" });

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<DocumentValidationException>();
    }

    [Fact]
    public async Task Handle_WhenSriAcceptsAndAuthorizes_ShouldReturnAuthorizedDocument()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var xml = GenerateTestXml();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(xml);
        _strategy.Setup(s => s.BuildRidePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed>xml</signed>");
        _sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", []));
        _sriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH123", DateTime.UtcNow, "AUTORIZADO", []));
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _eventRepo.Setup(r => r.CreateAsync(It.IsAny<DocumentEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentEvent e, CancellationToken _) => e);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.NotNull(result);
        result.Status.Should().Be(DocumentStatus.Authorized);
        result.AuthorizationNumber.Should().Be("AUTH123");
        result.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public async Task Handle_WhenSriRejects_ShouldReturnRejectedDocument()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var xml = GenerateTestXml();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(xml);
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed>xml</signed>");
        _sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(false, "DEVUELTA", ["CLAVE ACCESO REGISTRADA"]));
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _eventRepo.Setup(r => r.CreateAsync(It.IsAny<DocumentEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentEvent e, CancellationToken _) => e);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.NotNull(result);
        result.Status.Should().Be(DocumentStatus.Rejected);
        result.ErrorMessage.Should().Contain("CLAVE ACCESO REGISTRADA");
    }

    [Fact]
    public async Task Handle_WhenSriSendThrowsHttpRequestException_ShouldSaveDocumentWithFailedStatus()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var xml = GenerateTestXml();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(xml);
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed>xml</signed>");
        _sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _eventRepo.Setup(r => r.CreateAsync(It.IsAny<DocumentEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentEvent e, CancellationToken _) => e);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        // Verify document was saved with Failed status
        _documentRepo.Verify(r => r.CreateAsync(
            It.Is<Document>(d => d.Status == DocumentStatus.Failed),
            It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSriSendThrowsTaskCanceledException_ShouldSaveDocumentWithFailedStatus()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var xml = GenerateTestXml();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(xml);
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed>xml</signed>");
        _sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("Request timed out", new TimeoutException()));
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _eventRepo.Setup(r => r.CreateAsync(It.IsAny<DocumentEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentEvent e, CancellationToken _) => e);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();

        // Verify document was saved with Failed status
        _documentRepo.Verify(r => r.CreateAsync(
            It.Is<Document>(d => d.Status == DocumentStatus.Failed),
            It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSriSendFails_ShouldCreateFailedEventInAuditTrail()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var xml = GenerateTestXml();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(xml);
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed>xml</signed>");
        _sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SRI unavailable"));
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _eventRepo.Setup(r => r.CreateAsync(It.IsAny<DocumentEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentEvent e, CancellationToken _) => e);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        // Verify a Failed event was created in the audit trail
        _eventRepo.Verify(r => r.CreateAsync(
            It.Is<DocumentEvent>(e => e.EventType == EventType.Failed),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
