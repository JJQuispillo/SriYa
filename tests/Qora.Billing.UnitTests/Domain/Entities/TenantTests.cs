using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.UnitTests.Domain.Entities;

public class TenantTests
{
    [Fact]
    public void Create_ShouldInitializeWithActiveStatus()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp", "TestCo");

        tenant.IsActive.Should().BeTrue();
        tenant.Ruc.Value.Should().Be("1792268071001");
        tenant.BusinessName.Should().Be("Test Corp");
        tenant.TradeName.Should().Be("TestCo");
        tenant.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithInvalidRuc_ShouldThrowInvalidRucException()
    {
        var act = () => Tenant.Create("12345", "Test Corp");

        act.Should().Throw<InvalidRucException>();
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveToFalse()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");

        tenant.Deactivate();

        tenant.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_ShouldSetIsActiveToTrue()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.Deactivate();

        tenant.Activate();

        tenant.IsActive.Should().BeTrue();
    }

    [Fact]
    public void EnsureActive_WhenInactive_ShouldThrowTenantInactiveException()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.Deactivate();

        var act = () => tenant.EnsureActive();

        act.Should().Throw<TenantInactiveException>();
    }

    [Fact]
    public void EnsureActive_WhenActive_ShouldNotThrow()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");

        var act = () => tenant.EnsureActive();

        act.Should().NotThrow();
    }

    [Fact]
    public void Update_ShouldChangeBusinessNameAndTradeName()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");

        tenant.Update("New Corp", "NewCo");

        tenant.BusinessName.Should().Be("New Corp");
        tenant.TradeName.Should().Be("NewCo");
    }
}
