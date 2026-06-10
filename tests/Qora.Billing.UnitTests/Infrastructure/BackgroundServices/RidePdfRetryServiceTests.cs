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
/// Pruebas del RidePdfRetryService (ride-pdf-retry). Verifican el barrido
/// (GetAuthorizedMissingRidePdfAsync con los parámetros configurados — ventana de obsolescencia,
/// max retries, batch), la regeneración per-documento (genera + marca RideGeneratedAt + persiste),
/// el incremento de RideRetryCount ante fallo, y el manejo de errores per-documento (omitir + continuar).
/// </summary>
public class RidePdfRetryServiceTests
{
    private readonly RidePdfRetryOptions _options = new()
    {
        SweepIntervalSeconds = 300,
        StaleAfterSeconds = 120,
        MaxRetries = 5,
        MaxBatchSize = 50
    };

    [Fact]
    public async Task SweepAsync_WithNoDocuments_DoesNothing()
    {
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document>());

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object);
        await service.SweepAsync(CancellationToken.None);

        rideGen.Verify(g => g.GeneratePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
        docRepo.Verify(r => r.UpdateAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweepAsync_QueriesWithConfiguredStaleWindowMaxRetriesAndBatchSize()
    {
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document>());

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object);
        await service.SweepAsync(CancellationToken.None);

        docRepo.Verify(r => r.GetAuthorizedMissingRidePdfAsync(
            It.Is<DateTime>(d => d <= DateTime.UtcNow.AddSeconds(-_options.StaleAfterSeconds + 5)),
            _options.MaxRetries,
            _options.MaxBatchSize,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_WhenGenerationSucceeds_MarksRideGeneratedAndPersists()
    {
        var doc = CreateAuthorizedDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc });
        rideGen.Setup(g => g.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object);
        await service.SweepAsync(CancellationToken.None);

        doc.RideGeneratedAt.Should().NotBeNull();
        doc.RideRetryCount.Should().Be(0); // éxito al primer intento, no incrementa el contador
        rideGen.Verify(g => g.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
        docRepo.Verify(r => r.UpdateAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_WhenGenerationFails_IncrementsRetryCountAndPersistsWithoutMarking()
    {
        var doc = CreateAuthorizedDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc });
        rideGen.Setup(g => g.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("QuestPDF render failed"));
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object);
        // El error per-documento se omite a nivel de barrido (no debe propagar).
        await service.SweepAsync(CancellationToken.None);

        doc.RideGeneratedAt.Should().BeNull();   // NO se marcó: la generación falló
        doc.RideRetryCount.Should().Be(1);       // intento contabilizado
        doc.Status.Should().Be(DocumentStatus.Authorized); // estado intacto
        docRepo.Verify(r => r.UpdateAsync(doc, It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_WhenDocThrows_SkipsAndContinuesWithNext()
    {
        // El error al regenerar un documento se omite; los demás se procesan.
        var failing = CreateAuthorizedDocument();
        var ok = CreateAuthorizedDocument();
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { failing, ok });

        rideGen.Setup(g => g.GeneratePdfAsync(failing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("render boom"));
        rideGen.Setup(g => g.GeneratePdfAsync(ok, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object);
        await service.SweepAsync(CancellationToken.None);

        failing.RideGeneratedAt.Should().BeNull();  // falló y se omitió
        failing.RideRetryCount.Should().Be(1);
        ok.RideGeneratedAt.Should().NotBeNull();    // procesado igualmente
    }

    [Fact]
    public async Task SweepAsync_WhenRetriesReachMax_LogsExhausted()
    {
        // Con MaxRetries=5 y RideRetryCount=4, un fallo lo lleva a 5 → agotado.
        var doc = CreateAuthorizedDocument(rideRetryCount: 4);
        var docRepo = new Mock<IDocumentRepository>();
        var uow = new Mock<IUnitOfWork>();
        var rideGen = new Mock<IRideGenerator>();
        var logger = new Mock<ILogger<RidePdfRetryService>>();

        docRepo.Setup(r => r.GetAuthorizedMissingRidePdfAsync(
                It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Document> { doc });
        rideGen.Setup(g => g.GeneratePdfAsync(doc, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still failing"));
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = CreateService(docRepo.Object, uow.Object, rideGen.Object, logger);
        await service.SweepAsync(CancellationToken.None);

        doc.RideRetryCount.Should().Be(5);
        doc.RideGeneratedAt.Should().BeNull();
        // Verifica que se logueó el evento de agotamiento (EventId 2032 = RidePdfRetryExhausted).
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Id == 2032),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private RidePdfRetryService CreateService(
        IDocumentRepository docRepo,
        IUnitOfWork uow,
        IRideGenerator rideGen,
        Mock<ILogger<RidePdfRetryService>>? logger = null)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentRepository))).Returns(docRepo);
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(uow);
        serviceProvider.Setup(sp => sp.GetService(typeof(IRideGenerator))).Returns(rideGen);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new RidePdfRetryService(
            scopeFactory.Object,
            Options.Create(_options),
            (logger ?? new Mock<ILogger<RidePdfRetryService>>()).Object);
    }

    /// <summary>
    /// Crea un Document Authorized (sin RIDE generado). Opcionalmente fuerza RideRetryCount vía
    /// IncrementRideRetryCount para simular intentos previos.
    /// </summary>
    private static Document CreateAuthorizedDocument(int rideRetryCount = 0)
    {
        var doc = Document.Create(
            Guid.NewGuid(), DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1234567890001" },
            new Dictionary<string, string> { ["identificacion"] = "0987654321" });
        doc.SetXmlContent("<factura/>", new AccessKey(GenerateValidAccessKey()));
        doc.SetSignedXml("<signed/>");
        doc.MarkSentToSri();
        doc.Authorize("AUTH-1", DateTime.UtcNow);

        for (var i = 0; i < rideRetryCount; i++)
            doc.IncrementRideRetryCount();

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
