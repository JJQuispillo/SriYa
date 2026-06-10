using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Infrastructure.Persistence.Repositories;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// TI-3: los 3 caminos cross-tenant deliberados deben seguir funcionando bajo FORCE RLS + fail-closed,
/// porque corren sobre el rol billing_privileged (BYPASSRLS). Se ejercitan a través de los repositorios
/// reales (DocumentRepository, ApiKeyRepository) construidos con contextos del fixture, sin tenant fijado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PrivilegedPathTests
{
    private readonly PostgresFixture _fixture;

    public PrivilegedPathTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TI3_ApiKeyByHash_ResolvesAcrossTenants_BeforeTenantKnown()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // El repo usa el contexto privilegiado para GetByKeyHashAsync (auth antes de conocer el tenant).
        var (appCtxA, _) = _fixture.CreateAppContext(tenantId: null);
        await using var _a = appCtxA;
        await using var privA = _fixture.CreatePrivilegedContext();
        var repo = new ApiKeyRepository(appCtxA, privA);

        var keyForA = await repo.GetByKeyHashAsync(a.ApiKeyHash);
        var keyForB = await repo.GetByKeyHashAsync(b.ApiKeyHash);

        Assert.NotNull(keyForA);
        keyForA.TenantId.Should().Be(a.TenantId);

        Assert.NotNull(keyForB);
        keyForB.TenantId.Should().Be(b.TenantId);
    }

    [Fact]
    public async Task TI3_GetByAccessKey_FindsDocument_AcrossTenants_WithoutTenantContext()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        var (appCtx, _) = _fixture.CreateAppContext(tenantId: null);
        await using var _ac = appCtx;
        await using var priv = _fixture.CreatePrivilegedContext();
        var repo = new DocumentRepository(appCtx, priv);

        var docA = await repo.GetByAccessKeyAsync(a.AccessKey);
        var docB = await repo.GetByAccessKeyAsync(b.AccessKey);

        Assert.NotNull(docA);
        docA.TenantId.Should().Be(a.TenantId);

        Assert.NotNull(docB);
        docB.TenantId.Should().Be(b.TenantId);
    }

    [Fact]
    public async Task TI3_RetryScan_SeesPendingDocs_FromAllTenants()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        await TestDataSeeder.MarkPendingRetryAsync(_fixture, a.DocumentId);
        await TestDataSeeder.MarkPendingRetryAsync(_fixture, b.DocumentId);

        var (appCtx, _) = _fixture.CreateAppContext(tenantId: null);
        await using var _ac = appCtx;
        await using var priv = _fixture.CreatePrivilegedContext();
        var repo = new DocumentRepository(appCtx, priv);

        var pending = await repo.GetPendingRetryAsync(DateTime.UtcNow, maxResults: 100);

        var tenantIds = pending.Select(d => d.TenantId).ToList();
        tenantIds.Should().Contain(a.TenantId);
        tenantIds.Should().Contain(b.TenantId, "el escaneo all-tenant del retry debe ver documentos de ambos tenants");
    }

    [Fact]
    public async Task TI3_PrivilegedContext_SeesAllTenants_Directly()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        await using var priv = _fixture.CreatePrivilegedContext();
        var allTenantIds = await priv.Documents.IgnoreQueryFilters()
            .Select(d => d.TenantId).Distinct().ToListAsync();

        allTenantIds.Should().Contain(a.TenantId);
        allTenantIds.Should().Contain(b.TenantId);
    }
}
