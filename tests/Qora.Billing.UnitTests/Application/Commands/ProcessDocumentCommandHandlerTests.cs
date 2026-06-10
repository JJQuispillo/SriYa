using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Settings;
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
    private readonly Mock<IIdempotencyStore> _idempotencyStore = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<ProcessDocumentCommandHandler>> _logger = new();
    private readonly IdempotencySettings _idempotencySettings = new() { RetentionDays = 7 };
    // Pruebas generales del handler (guards, accept/reject, idempotencia, dedupe). El detalle de los
    // 5 checkpoints del flujo atómico vive en ProcessDocumentCommandHandlerAtomicityTests.
    private readonly EmissionOptions _emissionOptions = new() { AutoGenerateSecuencial = false };

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
        _signer.Object, _sriClient.Object, _idempotencyStore.Object, _unitOfWork.Object,
        Options.Create(_idempotencySettings),
        Options.Create(_emissionOptions),
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

    private static string GenerateTestAccessKey()
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
        return baseDigits + checkDigit;
    }

    private static BuildXmlResult GenerateTestBuildXmlResult()
    {
        var accessKey = GenerateTestAccessKey();
        var xml = $"<factura><infoTributaria><claveAcceso>{accessKey}</claveAcceso></infoTributaria></factura>";
        return new BuildXmlResult(xml, new Qora.Billing.Domain.ValueObjects.AccessKey(accessKey));
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
                new DocumentItemDto("PROD001", "Test Product", 2, 50.00m, 0, "2", "4")
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
        var buildResult = GenerateTestBuildXmlResult();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(buildResult);
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
        result.Estado.Should().Be(DocumentStatus.Authorized);
        result.NumeroAutorizacion.Should().Be("AUTH123");
        result.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public async Task Handle_WhenSriRejects_ShouldReturnRejectedDocument()
    {
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var buildResult = GenerateTestBuildXmlResult();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(buildResult);
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
        result.Estado.Should().Be(DocumentStatus.Rejected);
        result.MensajeError.Should().Contain("CLAVE ACCESO REGISTRADA");
    }

    // ─── Idempotencia (II-1): rama del header Idempotency-Key ──────────────────────

    [Fact]
    public async Task Handle_WhenIdempotencyKeyReplayedWithSameHash_ReturnsStoredResponse_WithoutReissuing()
    {
        // GIVEN un registro completado con el MISMO hash del request actual.
        var tenantId = Guid.NewGuid();
        var command = CreateValidCommand(tenantId) with { IdempotencyKey = "k1" };
        var requestHash = Qora.Billing.Application.Extensions.IdempotencyHasher.ComputeRequestHash(command.Request);

        var storedResponse = new DocumentResponse(
            Guid.NewGuid(), tenantId, DomainDocumentType.Factura, "1234567890",
            DocumentStatus.Authorized, "AUTH-ORIG", DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

        var entry = IdempotencyKey.Start(tenantId, "k1", requestHash, DateTime.UtcNow.AddDays(7));
        entry.Complete(JsonSerializer.Serialize(storedResponse), storedResponse.Id);

        _idempotencyStore.Setup(s => s.FindAsync("k1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        var handler = CreateHandler();

        // WHEN se reenvía con la misma clave y el mismo cuerpo.
        var result = await handler.Handle(command, CancellationToken.None);

        // THEN se devuelve la respuesta original SIN volver a emitir al SRI.
        result.Id.Should().Be(storedResponse.Id);
        result.NumeroAutorizacion.Should().Be("AUTH-ORIG");
        _tenantRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _sriClient.Verify(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _idempotencyStore.Verify(s => s.TryStartAsync(It.IsAny<IdempotencyKey>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenIdempotencyKeyReusedWithDifferentHash_ThrowsIdempotencyConflict()
    {
        // GIVEN un registro existente con un hash DISTINTO al del request actual.
        var tenantId = Guid.NewGuid();
        var command = CreateValidCommand(tenantId) with { IdempotencyKey = "k1" };

        var entry = IdempotencyKey.Start(tenantId, "k1", "hash-de-otro-cuerpo", DateTime.UtcNow.AddDays(7));
        entry.Complete("{}", Guid.NewGuid());

        _idempotencyStore.Setup(s => s.FindAsync("k1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        var handler = CreateHandler();

        // WHEN se reusa la clave con un cuerpo distinto.
        var act = () => handler.Handle(command, CancellationToken.None);

        // THEN se rechaza (IdempotencyConflictException → 422), sin emitir.
        await act.Should().ThrowAsync<IdempotencyConflictException>();
        _sriClient.Verify(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenIdempotencyKeyNew_IssuesAndPersistsSnapshot()
    {
        // GIVEN no existe registro previo para la clave → TryStart gana el lock.
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var buildResult = GenerateTestBuildXmlResult();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(buildResult);
        _strategy.Setup(s => s.BuildRidePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 0x25 });
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

        _idempotencyStore.Setup(s => s.FindAsync("k1", It.IsAny<CancellationToken>())).ReturnsAsync((IdempotencyKey?)null);
        _idempotencyStore.Setup(s => s.TryStartAsync(It.IsAny<IdempotencyKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id) with { IdempotencyKey = "k1" };

        var result = await handler.Handle(command, CancellationToken.None);

        // THEN emite normalmente y persiste el snapshot completado para futuros replays.
        result.Estado.Should().Be(DocumentStatus.Authorized);
        _idempotencyStore.Verify(s => s.TryStartAsync(It.IsAny<IdempotencyKey>(), It.IsAny<CancellationToken>()), Times.Once);
        _idempotencyStore.Verify(s => s.CompleteAsync(
            It.Is<IdempotencyKey>(e => e.IsCompleted && e.ResponseSnapshot != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenBusinessIdentityDuplicate_DedupesToExistingDocument_NotA500()
    {
        // GIVEN la persistencia choca con el unique de identidad de negocio (II-2).
        var tenant = CreateActiveTenant();
        var signature = CreateValidSignature(tenant.Id);
        var buildResult = GenerateTestBuildXmlResult();

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(signature);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(buildResult);
        _strategy.Setup(s => s.BuildRidePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 0x25 });
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

        // SaveChanges lanza el conflicto de identidad de negocio (como lo traduce UnitOfWork).
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateBusinessIdentityException("dup"));

        var existing = Document.Create(
            tenant.Id, DomainDocumentType.Factura,
            new Dictionary<string, string> { ["estab"] = "001", ["ptoEmi"] = "001", ["secuencial"] = "000000001" },
            new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" });
        _documentRepo.Setup(r => r.GetByBusinessIdentityAsync(
                It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var handler = CreateHandler();
        var command = CreateValidCommand(tenant.Id);

        // WHEN se emite un duplicado de identidad de negocio (sin idempotency key).
        var result = await handler.Handle(command, CancellationToken.None);

        // THEN se deduplica devolviendo el comprobante existente, NUNCA un 500.
        result.Id.Should().Be(existing.Id);
        _documentRepo.Verify(r => r.GetByBusinessIdentityAsync(
            It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
