using Microsoft.Extensions.DependencyInjection;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Verifies the test infrastructure (InMemory DB, DI) works correctly.
/// </summary>
[Collection("Integration")]
public class InfrastructureTests
{
    private readonly BillingApiFactory _factory;

    public InfrastructureTests(BillingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Database_ShouldSupportCrudOperations()
    {
        using var scope = _factory.Services.CreateScope();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Use a unique RUC to avoid conflicts with other tests
        var tenant = Qora.Billing.Domain.Entities.Tenant.Create("1799999999001", "Infra Test Corp");
        await tenantRepo.CreateAsync(tenant);
        await unitOfWork.SaveChangesAsync();

        var found = await tenantRepo.GetByIdAsync(tenant.Id);
        Assert.NotNull(found);
        Assert.Equal("Infra Test Corp", found!.BusinessName);
    }
}
