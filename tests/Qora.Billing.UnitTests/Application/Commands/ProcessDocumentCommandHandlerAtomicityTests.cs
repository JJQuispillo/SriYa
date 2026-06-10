using System.Collections.Generic;
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

/// <summary>
/// Pruebas del flujo atómico del handler de emisión: los 5 checkpoints
/// persisten (cada uno llama a SaveChangesAsync) y las fallas transitorias del SRI relanzan tras
/// persistir el último checkpoint (el reconciliador recoge el documento). Ver T-EMI-017..021.
/// </summary>
public class ProcessDocumentCommandHandlerAtomicityTests
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
    private readonly EmissionOptions _emissionOptions = new() { AutoGenerateSecuencial = false };

    public ProcessDocumentCommandHandlerAtomicityTests()
    {
        _strategy.Setup(s => s.DocumentType).Returns(DomainDocumentType.Factura);
        _strategy.Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GenerateTestBuildXmlResult());
        _strategy.Setup(s => s.BuildRidePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0x01]);
        _signer.Setup(s => s.SignDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed/>");
        _sriTaxCodeRepo.Setup(r => r.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SriTaxCode.Create("2", "4", 15m, "IVA 15%"));
        // Pre-reserva: no existe MAX previo → null (primera emisión de la identidad).
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private ProcessDocumentCommandHandler CreateHandler() => new(
        _tenantRepo.Object, _documentRepo.Object, _signatureRepo.Object,
        _eventRepo.Object, _sriTaxCodeRepo.Object,
        new[] { _strategy.Object },
        _signer.Object, _sriClient.Object, _idempotencyStore.Object, _unitOfWork.Object,
        Options.Create(_idempotencySettings),
        Options.Create(_emissionOptions),
        _logger.Object);

    private void SetupTenantAndSignature(Guid tenantId)
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ElectronicSignature.Create(tenantId, [0x01, 0x02], "password", "Owner", DateTime.UtcNow.AddYears(1)));
    }

    private static BuildXmlResult GenerateTestBuildXmlResult()
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
        var xml = $"<factura><infoTributaria><claveAcceso>{accessKey}</claveAcceso></infoTributaria></factura>";
        return new BuildXmlResult(xml, new Qora.Billing.Domain.ValueObjects.AccessKey(accessKey));
    }

    private static ProcessDocumentCommand CreateValidCommand(Guid tenantId)
        => CreateCommandWithSecuencial(tenantId, "000000001");

    private static ProcessDocumentCommand CreateCommandWithSecuencial(Guid tenantId, string secuencial)
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" }, { "razonSocial", "Test Corp" }, { "dirMatriz", "Quito" },
                { "estab", "001" }, { "ptoEmi", "001" }, { "secuencial", secuencial }
            },
            new Dictionary<string, string>
            {
                { "identificacion", "0102030405001" }, { "razonSocial", "Buyer Corp" }, { "tipoIdentificacion", "04" }
            },
            [ new DocumentItemDto("PROD001", "Test Product", 2, 50.00m, 0, "2", "4") ]);
        return new ProcessDocumentCommand(tenantId, request);
    }

    [Fact]
    public async Task Handle_HappyPath_PreReservesLocksAndPersistsFiveCheckpoints()
    {
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _sriClient.Setup(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", []));
        _sriClient.Setup(s => s.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-123", DateTime.UtcNow, "AUTORIZADO", []));

        var handler = CreateHandler();
        var response = await handler.Handle(CreateValidCommand(tenantId), CancellationToken.None);

        response.Estado.Should().Be(DocumentStatus.Authorized);
        // C1 toma el lock vía GetMaxSecuencialWithLockAsync.
        _documentRepo.Verify(r => r.GetMaxSecuencialWithLockAsync(
            tenantId, DomainDocumentType.Factura, "001", "001", It.IsAny<CancellationToken>()), Times.Once);
        // C1 inserta el Draft.
        _documentRepo.Verify(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Once);
        // Múltiples checkpoints → múltiples SaveChangesAsync (C1, C2, C3, C4, C5 + eventos/PDF).
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(5));
    }

    [Fact]
    public async Task Handle_WhenSriSendThrowsTransient_PersistsSignedAndRethrows()
    {
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _sriClient.Setup(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SRI caído"));

        var handler = CreateHandler();
        var act = () => handler.Handle(CreateValidCommand(tenantId), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        // Draft (C1) + XML (C2) + Signed (C3) persistidos antes de la falla transitoria.
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(3));
    }

    [Fact]
    public async Task Handle_WhenAuthCheckThrowsTransient_PersistsSentToSriAndRethrows()
    {
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _sriClient.Setup(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", []));
        _sriClient.Setup(s => s.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var handler = CreateHandler();
        var act = () => handler.Handle(CreateValidCommand(tenantId), CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
        // C4 (SentToSri) persistido antes de la falla en la verificación de autorización.
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(4));
    }

    // ── Monotonía exacta MAX+1 (REQ-EMI-008/010/012, S-EMI-003) ────────────────────────────────
    // El secuencial provisto por el cliente (AutoGenerateSecuencial=false) debe ser EXACTAMENTE
    // MAX+1. Tanto el hueco (> MAX+1) como el duplicado/regresión (<= MAX) se rechazan con
    // DocumentValidationException → 422 con el mensaje literal del spec.

    [Fact]
    public async Task Handle_WhenSecuencialIsAheadLeavingGap_ThrowsValidationWithExactMessage()
    {
        // S-EMI-003: MAX=000000005, provisto=000000007 (hueco) → esperaba 000000006.
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000005");

        var handler = CreateHandler();
        var act = () => handler.Handle(CreateCommandWithSecuencial(tenantId, "000000007"), CancellationToken.None);

        (await act.Should().ThrowAsync<DocumentValidationException>())
            .Which.Message.Should().Be("Secuencial fuera de orden: esperaba 000000006, recibió 000000007");
        // No se persiste ninguna fila (no se llega a CreateAsync/SaveChanges).
        _documentRepo.Verify(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenSecuencialIsBehindOrDuplicate_ThrowsValidationWithExactMessage()
    {
        // Regresión/duplicado: MAX=000000005, provisto=000000003 (<= MAX) → esperaba 000000006.
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000005");

        var handler = CreateHandler();
        var act = () => handler.Handle(CreateCommandWithSecuencial(tenantId, "000000003"), CancellationToken.None);

        (await act.Should().ThrowAsync<DocumentValidationException>())
            .Which.Message.Should().Be("Secuencial fuera de orden: esperaba 000000006, recibió 000000003");
        _documentRepo.Verify(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenSecuencialIsExactlyMaxPlusOne_IsAccepted()
    {
        // Happy-path monotonía: MAX=000000005, provisto=000000006 (= MAX+1) → aceptado.
        var tenantId = Guid.NewGuid();
        SetupTenantAndSignature(tenantId);
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000005");
        _sriClient.Setup(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", []));
        _sriClient.Setup(s => s.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-123", DateTime.UtcNow, "AUTORIZADO", []));

        var handler = CreateHandler();
        var response = await handler.Handle(CreateCommandWithSecuencial(tenantId, "000000006"), CancellationToken.None);

        response.Estado.Should().Be(DocumentStatus.Authorized);
        _documentRepo.Verify(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // Modo AUTO (server-side secuencial). Change #3 secuencial-server-side.
    // El tenant con AutoGenerateSecuencial=true delega la asignación del secuencial al servidor:
    // tras el lock FOR UPDATE el handler computa MAX+1 y lo asigna en columna + IssuerInfo["secuencial"]
    // ANTES de C1/C2. Helpers AUTO específicos abajo (tenant con flag + comando sin secuencial).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private void SetupAutoTenantAndSignature(Guid tenantId)
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.ConfigureSecuencialMode(true); // AUTO
        _tenantRepo.Setup(r => r.GetByIdAsync(tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _signatureRepo.Setup(r => r.GetActiveByTenantIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ElectronicSignature.Create(tenantId, [0x01, 0x02], "password", "Owner", DateTime.UtcNow.AddYears(1)));
    }

    private void SetupSriHappyPath()
    {
        _sriClient.Setup(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", []));
        _sriClient.Setup(s => s.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-123", DateTime.UtcNow, "AUTORIZADO", []));
    }

    // Comando AUTO: el emisor NO envía secuencial (clave 'secuencial' ausente del IssuerInfo).
    private static ProcessDocumentCommand CreateAutoCommandWithoutSecuencial(Guid tenantId)
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" }, { "razonSocial", "Test Corp" }, { "dirMatriz", "Quito" },
                { "estab", "001" }, { "ptoEmi", "001" } // ← sin 'secuencial'
            },
            new Dictionary<string, string>
            {
                { "identificacion", "0102030405001" }, { "razonSocial", "Buyer Corp" }, { "tipoIdentificacion", "04" }
            },
            [ new DocumentItemDto("PROD001", "Test Product", 2, 50.00m, 0, "2", "4") ]);
        return new ProcessDocumentCommand(tenantId, request);
    }

    [Fact]
    public async Task Handle_AutoMode_FirstEmission_AssignsSecuencial000000001()
    {
        // T-SEC-018 (R2, S "First auto emission"): set vacío (MAX=null) → el servidor asigna 000000001.
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);
        SetupSriHappyPath();
        // Base default ya devuelve null en GetMaxSecuencialWithLockAsync (primera emisión).

        Document? created = null;
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .Callback<Document, CancellationToken>((d, _) => created = d)
            .ReturnsAsync((Document d, CancellationToken _) => d);

        var handler = CreateHandler();
        var response = await handler.Handle(CreateAutoCommandWithoutSecuencial(tenantId), CancellationToken.None);

        response.Estado.Should().Be(DocumentStatus.Authorized);
        response.Secuencial.Should().Be("000000001", "primera emisión AUTO arranca en 000000001");
        Assert.NotNull(created);
        created.Secuencial.Should().Be("000000001");
    }

    [Fact]
    public async Task Handle_AutoMode_SubsequentEmission_AssignsMaxPlusOne_InColumnIssuerAndAccessKeyBuild()
    {
        // T-SEC-019 (R2, S "Subsequent auto emission"): MAX=000000007 → asigna 000000008 en columna,
        // IssuerInfo["secuencial"] Y es el valor que la estrategia lee al construir la clave de acceso.
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);
        SetupSriHappyPath();
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000007");

        // Captura lo que la estrategia (build de clave de acceso, C2) lee de IssuerInfo en el momento del build.
        string? secuencialSeenByAccessKeyBuild = null;
        _strategy.Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .Callback<Document, CancellationToken>((d, _) =>
                secuencialSeenByAccessKeyBuild = d.IssuerInfo.TryGetValue("secuencial", out var v) ? v : null)
            .ReturnsAsync(GenerateTestBuildXmlResult());

        Document? created = null;
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .Callback<Document, CancellationToken>((d, _) => created = d)
            .ReturnsAsync((Document d, CancellationToken _) => d);

        var handler = CreateHandler();
        var response = await handler.Handle(CreateAutoCommandWithoutSecuencial(tenantId), CancellationToken.None);

        response.Secuencial.Should().Be("000000008");
        Assert.NotNull(created);
        created.Secuencial.Should().Be("000000008", "columna Secuencial = MAX+1");
        created.IssuerInfo["secuencial"].Should().Be("000000008", "IssuerInfo['secuencial'] = MAX+1");
        secuencialSeenByAccessKeyBuild.Should().Be("000000008",
            "el build de la clave de acceso (C2) lee el secuencial asignado server-side ANTES de construir");
    }

    [Fact]
    public async Task Handle_AutoMode_OnUniqueViolation_RetriesWithRecomputedMaxPlusOne_DoesNotDedupe()
    {
        // T-SEC-020 (R3, S "Concurrent first emissions"): el primer insert choca con el unique
        // (DuplicateBusinessIdentityException); el handler recomputa MAX+1 bajo lock fresco y reintenta.
        // NUNCA deduplica al documento de otro (no se llama GetByBusinessIdentityAsync).
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);
        SetupSriHappyPath();

        // 1º lock → MAX=null (computa 000000001, colisiona). 2º lock → MAX=000000001 (computa 000000002, gana).
        _documentRepo.SetupSequence(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("000000001");

        var createCalls = 0;
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .Callback(() => createCalls++)
            .ReturnsAsync((Document d, CancellationToken _) => d);
        _unitOfWork.SetupSequence(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateBusinessIdentityException("dup", new Exception())) // C1 intento 1
            .ReturnsAsync(1).ReturnsAsync(1).ReturnsAsync(1).ReturnsAsync(1).ReturnsAsync(1).ReturnsAsync(1); // C1 intento 2 + C2..C5

        var handler = CreateHandler();
        var response = await handler.Handle(CreateAutoCommandWithoutSecuencial(tenantId), CancellationToken.None);

        response.Estado.Should().Be(DocumentStatus.Authorized);
        response.Secuencial.Should().Be("000000002", "el perdedor de la carrera avanza a MAX+1 recomputado");
        createCalls.Should().Be(2, "un reintento tras la colisión");
        _documentRepo.Verify(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "re-lock para recomputar MAX+1");
        // AUTO NUNCA deduplica: no consulta el documento existente por identidad de negocio.
        _documentRepo.Verify(r => r.GetByBusinessIdentityAsync(
            It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AutoMode_RetryExhaustion_ThrowsSecuencialExhaustedException()
    {
        // T-SEC-021 (R3, S "Retry exhaustion"): 5 colisiones consecutivas en C1 → SecuencialExhaustedException
        // (mapeada a 409 por GlobalExceptionHandler). Nunca deduplica.
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);
        SetupSriHappyPath();
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000001");
        _documentRepo.Setup(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document d, CancellationToken _) => d);
        // Cada SaveChangesAsync (C1) lanza la violación de unicidad → agota los 5 intentos.
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateBusinessIdentityException("dup", new Exception()));

        var handler = CreateHandler();
        var act = () => handler.Handle(CreateAutoCommandWithoutSecuencial(tenantId), CancellationToken.None);

        await act.Should().ThrowAsync<SecuencialExhaustedException>();
        _documentRepo.Verify(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5), "5 intentos acotados antes de agotar");
        _documentRepo.Verify(r => r.GetByBusinessIdentityAsync(
            It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AutoMode_WithClientSentSecuencial_ThrowsDocumentValidationException()
    {
        // T-SEC-022 (R4, S "Auto + client secuencial"): el tenant AUTO + el cliente envía un secuencial →
        // rechazo DocumentValidationException (422 conflicto) ANTES de emitir. No se sobre-escribe en silencio.
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);

        var handler = CreateHandler();
        // CreateCommandWithSecuencial SÍ envía 'secuencial' → conflicto en modo AUTO.
        var act = () => handler.Handle(CreateCommandWithSecuencial(tenantId, "000000005"), CancellationToken.None);

        await act.Should().ThrowAsync<DocumentValidationException>();
        // No se persiste ninguna fila (rechazo antes de C1).
        _documentRepo.Verify(r => r.CreateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Response_CarriesSecuencialEstabAndPtoEmi()
    {
        // T-SEC-026 (R5, S "Response carries assigned number"): la DocumentResponse expone Secuencial,
        // Estab y PtoEmi (MapToResponse los puebla desde document.Secuencial/Estab/PtoEmision) para que el
        // cliente conozca la numeración asignada sin parsear la ClaveAcceso. Validado en modo AUTO
        // (secuencial asignado server-side = 000000003).
        var tenantId = Guid.NewGuid();
        SetupAutoTenantAndSignature(tenantId);
        SetupSriHappyPath();
        _documentRepo.Setup(r => r.GetMaxSecuencialWithLockAsync(
                It.IsAny<Guid>(), It.IsAny<DomainDocumentType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("000000002");

        var handler = CreateHandler();
        var response = await handler.Handle(CreateAutoCommandWithoutSecuencial(tenantId), CancellationToken.None);

        response.Secuencial.Should().Be("000000003");
        response.Estab.Should().Be("001");
        response.PtoEmi.Should().Be("001");
    }
}
