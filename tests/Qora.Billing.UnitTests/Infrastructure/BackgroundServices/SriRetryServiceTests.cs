using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.BackgroundServices;

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
        var delay = SriRetryService.CalculateBackoffDelay(retryCount);

        delay.TotalMinutes.Should().BeApproximately(expectedMinutes, 0.01);
    }

    [Fact]
    public void CalculateBackoffDelay_AtRetryZero_ShouldBe5Minutes()
    {
        var delay = SriRetryService.CalculateBackoffDelay(0);

        delay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void CalculateBackoffDelay_ShouldNeverExceed4Hours()
    {
        for (int i = 0; i < 20; i++)
        {
            var delay = SriRetryService.CalculateBackoffDelay(i);
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(4),
                $"retry count {i} should not exceed 4 hour cap");
        }
    }

    [Fact]
    public void CalculateNextRetryTime_ShouldReturnFutureTime()
    {
        var before = DateTime.UtcNow;

        var nextRetry = SriRetryService.CalculateNextRetryTime(0);

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
        var service = CreateService(mockDocRepo.Object, mockSriClient.Object);

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

        var service = CreateService(mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.Authorized);
        document.SriAuthorizationNumber.Should().Be("AUTH-123");
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenSendRejected_ShouldScheduleRetry()
    {
        var document = CreatePendingRetryDocument(retryCount: 2);

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(false, "DEVUELTA", new[] { "Error de validación" }));

        var service = CreateService(mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.PendingRetry);
        document.RetryCount.Should().Be(3);
        document.NextRetryAt.Should().NotBeNull();
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenMaxRetriesExceeded_ShouldMarkFailed()
    {
        var document = CreatePendingRetryDocument(retryCount: 9); // Next attempt will be #10 (the max)

        var mockDocRepo = new Mock<IDocumentRepository>();
        mockDocRepo.Setup(r => r.GetPendingRetryAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { document });

        var mockSriClient = new Mock<ISriClient>();
        mockSriClient.Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SriSendResult(false, "DEVUELTA", new[] { "Persistent error" }));

        var service = CreateService(mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.Failed);
        document.ErrorMessage.Should().Contain("Max retries");
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

        var service = CreateService(mockDocRepo.Object, mockSriClient.Object);

        await service.ProcessPendingRetriesAsync(CancellationToken.None);

        document.Status.Should().Be(DocumentStatus.PendingRetry);
        document.RetryCount.Should().Be(2);
        mockDocRepo.Verify(r => r.UpdateAsync(document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void MaxRetries_ShouldBe10()
    {
        SriRetryService.MaxRetries.Should().Be(10);
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
        IDocumentRepository documentRepository,
        ISriClient sriClient)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentRepository)))
            .Returns(documentRepository);
        serviceProvider.Setup(sp => sp.GetService(typeof(ISriClient)))
            .Returns(sriClient);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var logger = new Mock<ILogger<SriRetryService>>();

        return new SriRetryService(scopeFactory.Object, logger.Object);
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
