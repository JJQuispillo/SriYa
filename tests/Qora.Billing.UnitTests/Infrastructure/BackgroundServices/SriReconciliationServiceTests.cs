using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.BackgroundServices;

namespace Qora.Billing.UnitTests.Infrastructure.BackgroundServices;

/// <summary>
/// Pruebas del reconciliador (sri-emision-atomicidad, design D4/D8). Verifican el barrido
/// (GetStaleSentToSriAsync con los parámetros configurados), la reconciliación per-documento
/// (Authorize / ScheduleRetry + persistencia), y el manejo de errores D8 (omitir + continuar).
/// </summary>
public class SriReconciliationServiceTests
{
    private readonly SriReconciliationOptions _options = new()
    {
        SweepIntervalSeconds = 120,
        StaleSentToSriAfterSeconds = 600,
        MaxBatchSize = 50
    };

    [Fact]
    public async Task ReconcileSweepAsync_QueriesStaleWithConfiguredOlderThanAndBatchSize()
    {
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetStaleSentToSriAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document>());

        var service = CreateService(docRepo.Object, uow.Object, sriClient.Object);
        await service.ReconcileSweepAsync(CancellationToken.None);

        docRepo.Verify(r => r.GetStaleSentToSriAsync(
            It.Is<DateTime>(d => d <= DateTime.UtcNow.AddSeconds(-_options.StaleSentToSriAfterSeconds + 5)),
            _options.MaxBatchSize,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileSweepAsync_WhenAuthorized_AuthorizesAndPersists()
    {
        var doc = CreateSentToSriDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetStaleSentToSriAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc });
        sriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-9", DateTime.UtcNow, "AUTORIZADO", []));
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, sriClient.Object);
        await service.ReconcileSweepAsync(CancellationToken.None);

        doc.Status.Should().Be(DocumentStatus.Authorized);
        docRepo.Verify(r => r.UpdateAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileSweepAsync_WhenNotYetAuthorized_SchedulesRetryAndPersists()
    {
        var doc = CreateSentToSriDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetStaleSentToSriAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc });
        sriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(false, null, null, "EN PROCESO", []));
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, sriClient.Object);
        await service.ReconcileSweepAsync(CancellationToken.None);

        doc.Status.Should().Be(DocumentStatus.PendingRetry);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileSweepAsync_WhenDocThrows_SkipsAndContinuesWithNext()
    {
        // D8: un error al reconciliar un documento se omite; los demás se procesan.
        var failing = CreateSentToSriDocument();
        var ok = CreateSentToSriDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var sriClient = new Mock<ISriClient>();

        docRepo.Setup(r => r.GetStaleSentToSriAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { failing, ok });

        // Ambos docs comparten la clave de acceso de prueba; distinguimos por orden de llamada:
        // el primer documento (failing) lanza; el segundo (ok) autoriza.
        sriClient.SetupSequence(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SRI 500"))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-OK", DateTime.UtcNow, "AUTORIZADO", []));
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, sriClient.Object);
        await service.ReconcileSweepAsync(CancellationToken.None);

        failing.Status.Should().Be(DocumentStatus.SentToSri); // intacto (se omitió)
        ok.Status.Should().Be(DocumentStatus.Authorized);     // procesado
    }

    private SriReconciliationService CreateService(
        IDocumentRepository docRepo, IUnitOfWork uow, ISriClient sriClient)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentRepository))).Returns(docRepo);
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(uow);
        serviceProvider.Setup(sp => sp.GetService(typeof(ISriClient))).Returns(sriClient);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new SriReconciliationService(
            scopeFactory.Object, Options.Create(_options), new Mock<ILogger<SriReconciliationService>>().Object);
    }

    private static Document CreateSentToSriDocument()
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1234567890001" },
            new Dictionary<string, string> { ["identificacion"] = "0987654321" });
        doc.SetXmlContent("<factura/>", new AccessKey(GenerateValidAccessKey()));
        doc.SetSignedXml("<signed/>");
        doc.MarkSentToSri();
        return doc;
    }

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
        checkDigit = checkDigit switch { 11 => 0, 10 => 1, _ => checkDigit };
        return baseDigits + checkDigit;
    }
}
