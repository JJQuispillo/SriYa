using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Custom WebApplicationFactory for integration tests.
/// Replaces PostgreSQL with EF Core InMemory provider and mocks external services (SRI, signing).
/// </summary>
public class BillingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"BillingTest_{Guid.NewGuid()}";

    public Mock<ISriClient> SriClientMock { get; } = new();
    public Mock<IDocumentSigner> DocumentSignerMock { get; } = new();
    public Mock<IDocumentTypeStrategy> DocumentTypeStrategyMock { get; } = new();

    /// <summary>
    /// The service token used for service-to-service authentication in tests.
    /// </summary>
    public const string TestServiceToken = "test-service-token-12345";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove ALL DbContext and EF-related registrations to avoid "multiple providers" error
            var toRemove = services.Where(d =>
            {
                var st = d.ServiceType.FullName ?? "";
                var it = d.ImplementationType?.FullName ?? "";
                return st.Contains("BillingDbContext") ||
                       st.Contains("DbContextOptions") ||
                       st.Contains("Npgsql") || it.Contains("Npgsql") ||
                       st.Contains("IDatabaseProvider") ||
                       st.Contains("EntityFrameworkCore.Infrastructure") ||
                       st.Contains("EntityFrameworkCore.Storage") ||
                       st.Contains("EntityFrameworkCore.Update") ||
                       st.Contains("EntityFrameworkCore.Query") ||
                       st.Contains("EntityFrameworkCore.Migrations") ||
                       st.Contains("EntityFrameworkCore.Metadata") ||
                       st.Contains("EntityFrameworkCore.Diagnostics") ||
                       st.Contains("EntityFrameworkCore.ChangeTracking") ||
                       st.Contains("EntityFrameworkCore.Internal") ||
                       st.Contains("EntityFrameworkCore.ValueGeneration");
            }).ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            // Remove hosted services (background retry service)
            services.RemoveAll<IHostedService>();

            // Add test-specific DbContexts with InMemory provider.
            // Both the default and the privileged context share the SAME InMemory database so the
            // cross-tenant repositories (which resolve BillingPrivilegedDbContext) see the same data.
            // TransactionIgnoredWarning is suppressed because TenantContextMiddleware opens an ambient
            // transaction that InMemory cannot honor (it is a no-op there).
            services.AddDbContext<BillingDbContext>((sp, options) =>
                options.UseInMemoryDatabase(_databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            services.AddDbContext<BillingPrivilegedDbContext>((sp, options) =>
                options.UseInMemoryDatabase(_databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // Replace external service dependencies with mocks
            services.RemoveAll<ISriClient>();
            services.AddSingleton(SriClientMock.Object);

            services.RemoveAll<IDocumentSigner>();
            services.AddSingleton(DocumentSignerMock.Object);

            services.RemoveAll<IDocumentTypeStrategy>();
            services.AddSingleton(DocumentTypeStrategyMock.Object);
        });

        // Override configuration for service token
        builder.UseSetting("ServiceAuth:ServiceToken", TestServiceToken);
    }

    public Task InitializeAsync()
    {
        _ = Services;
        return Task.CompletedTask;
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
