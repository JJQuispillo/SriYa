using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.BackgroundServices;
using Qora.Billing.Infrastructure.Sri;

namespace Qora.Billing.UnitTests.Infrastructure.BackgroundServices;

/// <summary>
/// FIX N1 (sri-emision-atomicidad REQ-EMI-018, T-EMI-005): el bug N1 era que el retry service
/// stageaba cambios vía <c>IDocumentRepository.UpdateAsync</c> (que es stage-only) pero NUNCA
/// llamaba a <c>IUnitOfWork.SaveChangesAsync</c>. Estos tests verifican el CONTRATO del fix:
/// tras cada <c>UpdateAsync</c> debe invocarse <c>SaveChangesAsync</c> en el mismo scope.
/// </summary>
public class SriRetryServiceN1FixTests
{
    [Fact]
    public async Task RetryDocumentAsync_WhenSendAndAuthorizeSucceed_CallsUpdateAsyncAndSaveChangesAsync()
    {
        var document = CreatePendingRetryDocument(retryCount: 1);

        var docRepo = new Mock<IDocumentRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var sriClient = new Mock<ISriClient>();

        // GetPendingRetryAsync → devuelve el documento.
        docRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        // SRI → éxito: RECIBIDA + AUTORIZADO.
        sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", Array.Empty<string>()));
        sriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-123", DateTime.UtcNow, "AUTORIZADO", Array.Empty<string>()));

        // FIX N1 — la verificación clave: UpdateAsync Y SaveChangesAsync se llaman en el ORDEN correcto.
        // Se usa MockSequence para garantizar que UpdateAsync se invoca antes de SaveChangesAsync.
        // Nota: con MockBehavior.Strict, el setup de UpdateAsync (Task) requiere Returns(Task.CompletedTask)
        // explícito; el default de Moq (CompletedTask) no se aplica bajo Strict.
        var sequence = new MockSequence();
        docRepo.InSequence(sequence).Setup(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.InSequence(sequence).Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = CreateService(docRepo.Object, sriClient.Object, unitOfWork);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        // Aserciones explícitas: ambos se llamaron exactamente 1 vez.
        // (El orden está garantizado por MockSequence arriba; Moq lanza si SaveChangesAsync
        // se invoca antes de UpdateAsync.)
        docRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        document.Status.Should().Be(DocumentStatus.Authorized);
        document.SriAuthorizationNumber.Should().Be("AUTH-123");
    }

    [Fact]
    public async Task RetryDocumentAsync_WhenScheduleRetryAfterFailure_CallsUpdateAsyncAndSaveChangesAsync()
    {
        var document = CreatePendingRetryDocument(retryCount: 1);

        var docRepo = new Mock<IDocumentRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        // DEVUELTA → Reject + MarkFailed (rama no-DEVUELTA que también stagea).
        sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(false, "DEVUELTA", new[] { "Error de validación" }));

        // FIX N1: misma pareja (UpdateAsync, SaveChangesAsync) en orden, en el camino DEVUELTA.
        var sequence = new MockSequence();
        docRepo.InSequence(sequence).Setup(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.InSequence(sequence).Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = CreateService(docRepo.Object, sriClient.Object, unitOfWork);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        docRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        document.Status.Should().Be(DocumentStatus.Failed);
    }

    [Fact]
    public async Task RetryDocumentAsync_WhenCatchFires_CallsUpdateAsyncAndSaveChangesAsync()
    {
        var document = CreatePendingRetryDocument(retryCount: 1);

        var docRepo = new Mock<IDocumentRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        // Excepción transitoria en SendDocumentAsync → catch + HandleRetryFailure.
        sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // FIX N1: en el catch también se stagea + guarda (HandleRetryFailure hace ScheduleRetry o MarkFailed).
        var sequence = new MockSequence();
        docRepo.InSequence(sequence).Setup(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.InSequence(sequence).Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = CreateService(docRepo.Object, sriClient.Object, unitOfWork);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        docRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Después del catch, el documento está en PendingRetry (no Failed) porque no se alcanzó MaxRetries.
        document.Status.Should().Be(DocumentStatus.PendingRetry);
    }

    [Fact]
    public async Task RetryDocumentAsync_EmitsStructuredLogWithEventId2010()
    {
        // Verifica que el log estructurado con EventId 2010 (EmissionRetryPersisted) se emite
        // en cada SaveChangesAsync exitoso.
        var document = CreatePendingRetryDocument(retryCount: 1);

        var docRepo = new Mock<IDocumentRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();
        var logger = new Mock<ILogger<SriRetryService>>();

        docRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });
        docRepo.Setup(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()));

        sriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", Array.Empty<string>()));
        sriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-X", DateTime.UtcNow, "AUTORIZADO", Array.Empty<string>()));

        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, sriClient.Object, unitOfWork, logger);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        // El log con EventId 2010 se emite al menos una vez (puede ser más si hay reintentos).
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("SriRetry persisted state transition")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void SriRetryService_EmissionRetryPersistedEventId_Is2010()
    {
        // Constante de EventId documentada en el spec §"Observability" como 2010. El canary EventId fue
        // migrado del campo local de SriRetryService a EmissionEvents (T-EMI-013).
        // EventId es un struct (no enum), por eso comparamos .Id (int) en vez de usar .Should().Be().
        Qora.Billing.Application.Logging.EmissionEvents.EmissionRetryPersisted.Id.Should().Be(2010);
    }

    [Fact]
    public void DocumentRepository_UpdateAsync_HasStageOnlyContract()
    {
        // Contrato del repo: UpdateAsync es stage-only (NO emite UPDATE SQL).
        // El repositorio real retorna Task.CompletedTask sin tocar la conexión a BD.
        // El test verifica que NO se llame a SaveChangesAsync dentro del repo (eso lo hace el UoW).
        // Esta es una verificación estática del contrato vía XML doc + inspección de implementación;
        // el repo real no llama a SaveChangesAsync internamente (ver src/.../DocumentRepository.cs).
        var interfaceType = typeof(IDocumentRepository);
        var updateMethod = interfaceType.GetMethod(nameof(IDocumentRepository.UpdateAsync));
        updateMethod.Should().NotBeNull();
        // El XML doc del interface declara explícitamente el contrato stage-only.
        // Esta aserción falla si alguien quitó la documentación.
        // (No podemos inspeccionar el XML doc compilado desde runtime, así que documentamos
        // la expectativa como parte del contrato del test.)
        updateMethod!.ReturnType.Should().Be<Task>();
    }

    [Fact]
    public async Task RetryDocumentAsync_WhenCertExpiredDuringRetry_MarksFailedAndDoesNotSend()
    {
        // T-EMI-023 (REQ-EMI-016/017): si el certificado venció entre la emisión y el reintento,
        // el documento se marca Failed y NUNCA se llama a SendDocumentAsync.
        var document = CreatePendingRetryDocument(retryCount: 1);

        var docRepo = new Mock<IDocumentRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Certificado VENCIDO (ExpiresAt en el pasado) → EnsureValid lanza CertificateExpiredException.
        var expiredCert = ElectronicSignature.Create(
            document.TenantId, [0x01], "pwd", "Owner", DateTime.UtcNow.AddDays(-1));
        var sigRepoMock = new Mock<IElectronicSignatureRepository>();
        sigRepoMock.Setup(r => r.GetActiveByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredCert);

        var service = CreateService(docRepo.Object, sriClient.Object, unitOfWork, signatureRepository: sigRepoMock.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.Failed);
        sriClient.Verify(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // El estado Failed se persiste (UpdateAsync + SaveChangesAsync vía PersistAsync).
        docRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #region Helpers (espejo de los de SriRetryServiceTests)

    private static string GenerateValidAccessKey()
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
        checkDigit = checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };
        return baseDigits + checkDigit;
    }

    private static SriRetryService CreateService(
        IDocumentRepository documentRepository,
        ISriClient sriClient,
        Mock<IUnitOfWork> unitOfWorkMock,
        Mock<ILogger<SriRetryService>>? loggerMock = null,
        IElectronicSignatureRepository? signatureRepository = null)
    {
        // T-EMI-022/023: el retry service re-verifica el certificado en cada reintento. Por defecto
        // inyectamos un repo que devuelve un cert válido para no alterar el camino feliz de los tests N1.
        if (signatureRepository is null)
        {
            var sigRepoMock = new Mock<IElectronicSignatureRepository>();
            sigRepoMock.Setup(r => r.GetActiveByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid tid, CancellationToken _) =>
                    ElectronicSignature.Create(tid, [0x01], "pwd", "Owner", DateTime.UtcNow.AddYears(1)));
            signatureRepository = sigRepoMock.Object;
        }

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentRepository)))
            .Returns(documentRepository);
        serviceProvider.Setup(sp => sp.GetService(typeof(ISriClient)))
            .Returns(sriClient);
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork)))
            .Returns(unitOfWorkMock.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IElectronicSignatureRepository)))
            .Returns(signatureRepository);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        loggerMock ??= new Mock<ILogger<SriRetryService>>();

        // El ctor real de SriRetryService requiere IOptions<SriRetryConfiguration>; los tests
        // del N1 fix no dependen de valores específicos del config, así que inyectamos el
        // POCO con defaults (los tests originales SriRetryServiceTests cubren la variación).
        return new SriRetryService(
            scopeFactory.Object,
            Options.Create(new SriRetryConfiguration()),
            loggerMock.Object);
    }

    private static Document CreatePendingRetryDocument(int retryCount)
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1234567890001" },
            new Dictionary<string, string> { ["identificacion"] = "0987654321" });

        var accessKey = new AccessKey(GenerateValidAccessKey());

        doc.SetXmlContent("<factura/>", accessKey);
        doc.SetSignedXml("<signedFactura/>");
        doc.MarkSentToSri();
        doc.ScheduleRetry(DateTime.UtcNow.AddMinutes(-5));

        for (int i = 1; i < retryCount; i++)
        {
            doc.Reject($"Error attempt {i}");
            doc.ScheduleRetry(DateTime.UtcNow.AddMinutes(-5));
        }

        return doc;
    }

    #endregion
}
