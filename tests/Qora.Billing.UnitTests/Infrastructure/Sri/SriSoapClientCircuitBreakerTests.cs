using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Polly.CircuitBreaker;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Infrastructure.Sri;
using Xunit;

namespace Qora.Billing.UnitTests.Infrastructure.Sri;

public class SriSoapClientCircuitBreakerTests
{
    private static SriConfiguration DefaultConfig() => new()
    {
        Environment = EnvironmentType.Test,
        RecepcionUrl = "https://sri.test/recepcion",
        AutorizacionUrl = "https://sri.test/autorizacion",
        CircuitBreakerBreakDurationSeconds = 30
    };

    [Fact]
    public async Task SendDocumentAsync_WhenBrokenCircuitException_TranslatesToSriCircuitOpenException()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new BrokenCircuitException("test broken circuit"));

        var client = new HttpClient(handler.Object);
        var sut = new SriSoapClient(client, Options.Create(DefaultConfig()), NullLogger<SriSoapClient>.Instance);

        var ex = await Assert.ThrowsAsync<SriCircuitOpenException>(
            () => sut.SendDocumentAsync("<xml/>", CancellationToken.None));

        Assert.IsType<BrokenCircuitException>(ex.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.BreakDuration);
        Assert.Equal("fallos sostenidos", ex.Reason);
    }

    [Fact]
    public async Task CheckAuthorizationAsync_WhenBrokenCircuitException_TranslatesSimilarly()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new BrokenCircuitException("test broken circuit"));

        var client = new HttpClient(handler.Object);
        var sut = new SriSoapClient(client, Options.Create(DefaultConfig()), NullLogger<SriSoapClient>.Instance);

        var ex = await Assert.ThrowsAsync<SriCircuitOpenException>(
            () => sut.CheckAuthorizationAsync(
                "1234567890123456789012345678901234567890123456789", CancellationToken.None));

        Assert.IsType<BrokenCircuitException>(ex.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.BreakDuration);
    }

    [Fact]
    public async Task SendDocumentAsync_WhenIsolatedCircuitException_TranslatesWithManualReason()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new IsolatedCircuitException("test isolated"));

        var client = new HttpClient(handler.Object);
        var sut = new SriSoapClient(client, Options.Create(DefaultConfig()), NullLogger<SriSoapClient>.Instance);

        var ex = await Assert.ThrowsAsync<SriCircuitOpenException>(
            () => sut.SendDocumentAsync("<xml/>", CancellationToken.None));

        Assert.IsType<IsolatedCircuitException>(ex.InnerException);
        Assert.Equal("circuito aislado manualmente", ex.Reason);
        Assert.Equal(TimeSpan.Zero, ex.BreakDuration);
    }
}
