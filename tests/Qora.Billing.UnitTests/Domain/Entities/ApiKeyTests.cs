using FluentAssertions;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.UnitTests.Domain.Entities;

public class ApiKeyTests
{
    [Fact]
    public void Create_ShouldInitializeWithActiveStatus()
    {
        var tenantId = Guid.NewGuid();

        var apiKey = ApiKey.Create(tenantId, "hash123", "Production Key");

        apiKey.IsActive.Should().BeTrue();
        apiKey.TenantId.Should().Be(tenantId);
        apiKey.KeyHash.Should().Be("hash123");
        apiKey.Name.Should().Be("Production Key");
        apiKey.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void IsValid_WhenActiveAndNotExpired_ShouldReturnTrue()
    {
        var apiKey = ApiKey.Create(Guid.NewGuid(), "hash123", "Key",
            DateTime.UtcNow.AddDays(30));

        apiKey.IsValid().Should().BeTrue();
    }

    [Fact]
    public void IsValid_WhenDeactivated_ShouldReturnFalse()
    {
        var apiKey = ApiKey.Create(Guid.NewGuid(), "hash123", "Key");
        apiKey.Deactivate();

        apiKey.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsExpired_WhenPastExpirationDate_ShouldReturnTrue()
    {
        var apiKey = ApiKey.Create(Guid.NewGuid(), "hash123", "Key",
            DateTime.UtcNow.AddDays(-1));

        apiKey.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WithNoExpirationDate_ShouldReturnFalse()
    {
        var apiKey = ApiKey.Create(Guid.NewGuid(), "hash123", "Key");

        apiKey.IsExpired().Should().BeFalse();
    }
}
