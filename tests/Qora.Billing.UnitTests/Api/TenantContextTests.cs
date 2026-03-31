using FluentAssertions;
using Qora.Billing.Application.Interfaces;

namespace Qora.Billing.UnitTests.Api;

public class TenantContextTests
{
    [Fact]
    public void TenantContext_SetTenantId_ShouldStoreValue()
    {
        // Arrange
        var tenantContext = new TenantContext();
        var tenantId = Guid.NewGuid();

        // Act
        tenantContext.SetTenantId(tenantId);

        // Assert
        tenantContext.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public void TenantContext_DefaultTenantId_ShouldBeNull()
    {
        // Arrange & Act
        var tenantContext = new TenantContext();

        // Assert
        tenantContext.TenantId.Should().BeNull();
    }
}
