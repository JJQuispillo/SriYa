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

public class SriRetryServiceTests
{
    [Theory]
    [InlineData(0, 5)]       // 5 min
    [InlineData(1, 10)]      // 10 min
    [InlineData(2, 20)]      // 20 min
    [InlineData(3, 40)]      // 40 min
    [InlineData(4, 80)]      // 1h 20m
    [InlineData(5, 160)]     // 2h 40m
    [InlineData(6, 240)]     // 4h (cap)
    [InlineData(7, 240)]     // 4h (cap)
    [InlineData(9, 240)]     // 4h (cap)
    public void CalculateBackoffDelay_ShouldUseExponentialBackoffWithCap(int retryCount, double expectedMinutes)
    {
        var service = CreateService(new SriRetryConfiguration());

        var delay = service.CalculateBackoffDelay(retryCount);

        delay.TotalMinutes.Should().BeApproximately(expectedMinutes, 0.01);
    }

    [Fact]
    public void CalculateBackoffDelay_AtRetryZero_ShouldBe5Minutes()
    {
        var service = CreateService(new SriRetryConfiguration());

        var delay = service.CalculateBackoffDelay(0);

        delay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void CalculateBackoffDelay_ShouldNeverExceed4Hours()
    {
        var service = CreateService(new SriRetryConfiguration());

        for (int i = 0; i < 20; i++)
        {
            var delay = service.CalculateBackoffDelay(i);
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(4),
                $"retry count {i} should not exceed 4 hour cap");
        }
    }

    [Fact]
    public void CalculateNextRetryTime_ShouldReturnFutureTime()
    {
        var service = CreateService(new SriRetryConfiguration());

        var before = DateTime.UtcNow;

        var nextRetry = service.CalculateNextRetryTime(0);

        nextRetry.Should().BeAfter(before);
        nextRetry.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WithNoDocuments_ShouldDoNothing()
    {
        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Document>());

        var mockSriClient = new Mock<ISriClient>();
        var service = CreateService(new SriRetryConfiguration(), mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        mockSriClient.Verify(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenAuthorized_ShouldUpdateDocument()
    {
        var document = CreatePendingRetryDocument(retryCount: 1);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(true, "RECIBIDA", Array.Empty<string>()));
        mockSriClient.Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriAuthorizationResult(true, "AUTH-123", DateTime.UtcNow, "AUTORIZADO", Array.Empty<string>()));

        var service = CreateService(new SriRetryConfiguration(), mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.Authorized);
        document.SriAuthorizationNumber.Should().Be("AUTH-123");
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenSendReturnsDevuelta_ShouldMarkFailedPermanently()
    {
        var document = CreatePendingRetryDocument(retryCount: 2);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(false, "DEVUELTA", new[] { "Error de validación" }));

        var service = CreateService(new SriRetryConfiguration(), mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        // DEVUELTA = error de contenido; reenviar el mismo XML firmado siempre falla
        // (Ficha Técnica SRI §5.10), por lo que es una falla permanente: NO se programa reintento.
        document.Status.Should().Be(DocumentStatus.Failed);
        document.ErrorMessage.Should().Contain("DEVUELTA");
        document.ErrorMessage.Should().Contain("corrección manual");
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenMaxRetriesExceeded_ShouldMarkFailed()
    {
        // Con MaxRetries=10 (default), un documento en retryCount=9 falla al intentar el #10
        // (document.RetryCount+1=10 >= 10).
        var document = CreatePendingRetryDocument(retryCount: 9);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        // Una falla transitoria (excepción) recorre el path de reintento; al alcanzar el
        // máximo de intentos (#10), se marca como Failed con el mensaje de máximo superado.
        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Persistent error"));

        var service = CreateService(new SriRetryConfiguration(), mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.Failed);
        document.ErrorMessage.Should().Contain("máximo de reintentos");
        document.ErrorMessage.Should().Contain("Persistent error");
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenExceptionThrown_ShouldScheduleRetry()
    {
        var document = CreatePendingRetryDocument(retryCount: 1);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = CreateService(new SriRetryConfiguration(), mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.PendingRetry);
        document.RetryCount.Should().Be(2);
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // T-CFG-022: SriRetryService reads from IOptions<SriRetryConfiguration>
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithCustomConfig_StoresConfigValues()
    {
        // Verifica que el constructor acepta y almacena la SriRetryConfiguration inyectada.
        // El startup log (Information) se valida por code review; los valores del config
        // se verifican funcionalmente en el resto de tests de este archivo.
        var customConfig = new SriRetryConfiguration
        {
            PollingIntervalSeconds = 120,
            MaxRetries = 5,
            BaseDelaySeconds = 600,   // 10 min
            MaxDelaySeconds = 7200    // 2 h
        };

        var service = CreateService(customConfig);

        // Verify backoff delay calculation uses BaseDelay and MaxDelay from config
        service.CalculateBackoffDelay(0).TotalMinutes.Should().BeApproximately(10, 0.01);
        service.CalculateBackoffDelay(1).TotalMinutes.Should().BeApproximately(20, 0.01);
        service.CalculateBackoffDelay(2).TotalMinutes.Should().BeApproximately(40, 0.01);
        service.CalculateBackoffDelay(3).TotalMinutes.Should().BeApproximately(80, 0.01);
        service.CalculateBackoffDelay(4).TotalMinutes.Should().BeApproximately(120, 0.01);
        // 10 * 2^7 = 1280 min capped at 120 min (7200s / 60)
        service.CalculateBackoffDelay(7).TotalMinutes.Should().BeApproximately(120, 0.01);
    }

    [Fact]
    public void CalculateBackoffDelay_WithCustomConfig_UsesBaseAndMaxDelayFromConfig()
    {
        // BaseDelay=120s (2 min), MaxDelay=600s (10 min)
        // retryCount=0 → 2 min; retryCount=1 → 4 min; retryCount=2 → 8 min;
        // retryCount=3 → 16 min capped at 10 min; retryCount=4 → 32 min capped at 10 min.
        var customConfig = new SriRetryConfiguration
        {
            BaseDelaySeconds = 120,
            MaxDelaySeconds = 600
        };
        var service = CreateService(customConfig);

        service.CalculateBackoffDelay(0).TotalMinutes.Should().BeApproximately(2, 0.01);
        service.CalculateBackoffDelay(1).TotalMinutes.Should().BeApproximately(4, 0.01);
        service.CalculateBackoffDelay(2).TotalMinutes.Should().BeApproximately(8, 0.01);
        service.CalculateBackoffDelay(3).TotalMinutes.Should().BeApproximately(10, 0.01); // cap
        service.CalculateBackoffDelay(4).TotalMinutes.Should().BeApproximately(10, 0.01); // cap
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WithCustomMaxRetries_RespectsConfiguredValue()
    {
        // Con MaxRetries=3, un documento en retryCount=2 falla al intentar el #3
        // (document.RetryCount+1=3 >= 3).
        var customConfig = new SriRetryConfiguration
        {
            MaxRetries = 3,
            BaseDelaySeconds = 60,
            MaxDelaySeconds = 3600
        };
        var document = CreatePendingRetryDocument(retryCount: 2);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Persistent error"));

        var service = CreateService(customConfig, mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        // El mensaje debe mencionar el valor configurado (3), no el default (10).
        document.Status.Should().Be(DocumentStatus.Failed);
        document.ErrorMessage.Should().Contain("máximo de reintentos (3)");
    }

    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        // Contrato explícito: los defaults son idénticos al baseline hardcoded.
        var defaults = new SriRetryConfiguration();

        defaults.PollingIntervalSeconds.Should().Be(60);
        defaults.MaxRetries.Should().Be(10);
        defaults.BaseDelaySeconds.Should().Be(300);
        defaults.MaxDelaySeconds.Should().Be(14400);
    }

    #region Helpers

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
        SriRetryConfiguration config,
        IDocumentRepository? documentRepository = null,
        ISriClient? sriClient = null,
        Mock<IUnitOfWork>? unitOfWorkMock = null)
    {
        // FIX N1 (T-EMI-004 / T-EMI-005): el retry service resuelve IUnitOfWork desde el scope por documento.
        // Si los tests que verifican el contrato de SaveChangesAsync pasan un mock, lo registramos
        // en el service provider; en caso contrario registramos un mock vacío que satisface
        // GetRequiredService y evita NullReferenceException en los tests existentes.
        unitOfWorkMock ??= new Mock<IUnitOfWork>();

        var serviceProvider = new Mock<IServiceProvider>();
        if (documentRepository != null)
        {
            serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentRepository)))
                .Returns(documentRepository);
        }
        if (sriClient != null)
        {
            serviceProvider.Setup(sp => sp.GetService(typeof(ISriClient)))
                .Returns(sriClient);
        }
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork)))
            .Returns(unitOfWorkMock.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IElectronicSignatureRepository)))
            .Returns(CreateValidSignatureRepo());

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var logger = new Mock<ILogger<SriRetryService>>();

        return new SriRetryService(scopeFactory.Object, Options.Create(config), logger.Object);
    }

    // T-EMI-022/023: repo de firmas que devuelve un cert válido (camino feliz de los tests existentes).
    private static IElectronicSignatureRepository CreateValidSignatureRepo()
    {
        var mock = new Mock<IElectronicSignatureRepository>();
        mock.Setup(r => r.GetActiveByTenantIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tid, CancellationToken _) =>
                ElectronicSignature.Create(tid, [0x01], "pwd", "Owner", DateTime.UtcNow.AddYears(1)));
        return mock.Object;
    }

    private static SriRetryService CreateServiceWithLogger(
        SriRetryConfiguration config,
        ILogger<SriRetryService> logger)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IElectronicSignatureRepository)))
            .Returns(CreateValidSignatureRepo());
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new SriRetryService(scopeFactory.Object, Options.Create(config), logger);
    }

    /// <summary>
    /// Creates a Document in PendingRetry status with the given retry count.
    /// Uses reflection to set internal state since Document uses private setters.
    /// </summary>
    private static Document CreatePendingRetryDocument(int retryCount)
    {
        var doc = Document.Create(
            Guid.NewGuid(),
            DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1234567890001" },
            new Dictionary<string, string> { ["identificacion"] = "0987654321" });

        // Generate a valid 49-char access key with correct Mod11 check digit
        var accessKey = new AccessKey(GenerateValidAccessKey());

        doc.SetXmlContent("<factura/>", accessKey);
        doc.SetSignedXml("<signedFactura/>");
        doc.MarkSentToSri();

        // Schedule initial retry to get into PendingRetry status
        doc.ScheduleRetry(DateTime.UtcNow.AddMinutes(-5));

        // Schedule additional retries to reach desired retry count
        // Each ScheduleRetry call increments RetryCount by 1, and we already did 1
        for (int i = 1; i < retryCount; i++)
        {
            // Need to transition through a valid state to schedule retry again
            // PendingRetry -> Rejected -> PendingRetry
            doc.Reject($"Error attempt {i}");
            doc.ScheduleRetry(DateTime.UtcNow.AddMinutes(-5));
        }

        return doc;
    }

    #endregion
}
