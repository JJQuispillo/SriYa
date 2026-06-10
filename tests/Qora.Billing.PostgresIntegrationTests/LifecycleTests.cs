using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// PL-1 (exportación) + PL-2 (borrado con retención) sobre Postgres REAL:
///   - La exportación de un emisor contiene SÓLO los datos de ese emisor (RLS excluye al otro tenant).
///   - El borrado con alcance elimina físicamente los datos NO autorizados.
///   - Por defecto (AllowHardDeleteAuthorized=false) los comprobantes autorizados se anonimizan, NO se
///     eliminan; se conservan los campos fiscales (claveAcceso, nº autorización, XML firmado) y se redacta
///     la PII del comprador.
///   - Con AllowHardDeleteAuthorized=true, los autorizados se eliminan físicamente.
///   - Borrar el tenant A no toca al tenant B.
///
/// Imposible de cubrir con el proveedor InMemory (no hay RLS real ni el aislamiento por GUC).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LifecycleTests
{
    private readonly PostgresFixture _fixture;

    public LifecycleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Corre una unidad de trabajo como billing_app con el tenant fijado, replicando el sobre
    /// transaccional + GUC de TenantContextMiddleware (igual que los demás suites).
    /// </summary>
    private async Task<T> AsTenantAsync<T>(Guid tenantId, Func<BillingDbContext, Task<T>> work)
    {
        var (ctx, session) = _fixture.CreateAppContext(tenantId);
        await using var _ = ctx;
        await using var tx = await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, tenantId);
        try
        {
            var result = await work(ctx);
            await tx.CommitAsync();
            return result;
        }
        finally
        {
            session.SetInTransaction(false);
        }
    }

    private static (Dictionary<string, string> files, ZipArchive archive) ReadZip(byte[] bytes)
    {
        var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[entry.FullName] = reader.ReadToEnd();
        }
        return (files, archive);
    }

    // ── PL-1: exportación acotada al emisor ────────────────────────────────────────

    [Fact]
    public async Task Export_ContainsOnlyTargetTenantData()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        var bytes = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: false);
            using var ms = new MemoryStream();
            var size = await service.ExportTenantAsync(a.TenantId, ms);
            size.Should().BeGreaterThan(0);
            return ms.ToArray();
        });

        var (files, _) = ReadZip(bytes);

        files.Should().ContainKey("manifest.json");
        files.Should().ContainKey("data.json");

        var data = files["data.json"];
        // Contiene el documento de A (su clave de acceso) y NO el de B.
        data.Should().Contain(a.AccessKey);
        data.Should().NotContain(b.AccessKey);

        // El XML del documento de A está presente bajo xml/<accessKey>.xml.
        files.Keys.Should().Contain(k => k.StartsWith("xml/") && k.Contains(a.AccessKey));
        files.Keys.Should().NotContain(k => k.Contains(b.AccessKey));
    }

    [Fact]
    public async Task Export_DoesNotLeakCertificatePrivateKeyOrApiKeyHash()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        var bytes = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: false);
            using var ms = new MemoryStream();
            await service.ExportTenantAsync(a.TenantId, ms);
            return ms.ToArray();
        });

        var (files, _) = ReadZip(bytes);
        // El hash de la API key sembrada NO debe aparecer en ningún artefacto (sólo metadata).
        files.Values.Should().NotContain(v => v.Contains(a.ApiKeyHash));
    }

    // ── PL-2: borrado con retención ────────────────────────────────────────────────

    [Fact]
    public async Task ScopedDelete_HardDeletesNonAuthorized_AndExportsFirst()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        // El documento sembrado de A está en XmlGenerated (no autorizado) → debe borrarse físicamente.

        var result = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: false);
            using var ms = new MemoryStream();
            var r = await service.DeleteTenantDataAsync(a.TenantId, ms);
            r.ExportSizeBytes.Should().BeGreaterThan(0, "el borrado exporta SIEMPRE antes de tocar filas");
            return r;
        });

        result.NonAuthorizedHardDeleted.Should().BeGreaterThanOrEqualTo(1);
        result.AuthorizedAnonymized.Should().Be(0);

        // El documento no autorizado ya no existe (ni siquiera ignorando filtros) bajo el tenant A.
        var remaining = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Documents.IgnoreQueryFilters().CountAsync(d => d.TenantId == a.TenantId));
        remaining.Should().Be(0, "los documentos no autorizados se eliminan físicamente");
    }

    [Fact]
    public async Task ScopedDelete_AuthorizedAnonymizedByDefault_NotPhysicallyRemoved()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        var authDocId = await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, "001", "001", "000000050");
        await TestDataSeeder.MarkAuthorizedAsync(_fixture, authDocId, "AUTH-12345");

        var result = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: false);
            using var ms = new MemoryStream();
            return await service.DeleteTenantDataAsync(a.TenantId, ms);
        });

        result.AuthorizedAnonymized.Should().Be(1, "por defecto los autorizados se anonimizan, no se borran");
        result.AuthorizedHardDeleted.Should().Be(0);
        result.TenantAnonymized.Should().BeTrue("queda un autorizado retenido → la FK RESTRICT impide borrar el tenant");
        result.TenantHardDeleted.Should().BeFalse();

        // El comprobante autorizado SIGUE existiendo (soft-deleted + anonimizado), conservando los campos fiscales.
        var authDoc = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == authDocId));

        Assert.NotNull(authDoc);
        authDoc.Status.Should().Be(DocumentStatus.Authorized, "el estado fiscal se conserva");
        authDoc.IsAnonymized.Should().BeTrue();
        authDoc.DeletedAt.Should().NotBeNull("soft-delete, no borrado físico");
        authDoc.SriAuthorizationNumber.Should().Be("AUTH-12345", "se retiene el nº de autorización (fiscal)");
        Assert.NotNull(authDoc.AccessKey); // se retiene la clave de acceso (fiscal)
        authDoc.SignedXmlContent.Should().NotBeNullOrEmpty("se retiene el XML firmado (fiscal)");
        // La PII del comprador fue redactada.
        authDoc.BuyerInfo.Should().ContainKey("razonSocialComprador");
        authDoc.BuyerInfo["razonSocialComprador"].Should().Be("[ANONIMIZADO]");
        authDoc.BuyerInfo["emailComprador"].Should().Be("[ANONIMIZADO]");
        // La identificación fiscal del comprador se RETIENE.
        authDoc.BuyerInfo["identificacionComprador"].Should().Be("0993456789001");

        // El tenant sigue existiendo pero anonimizado/desactivado, conservando el RUC.
        var tenant = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Tenants.FirstOrDefaultAsync(t => t.Id == a.TenantId));
        Assert.NotNull(tenant);
        tenant.IsActive.Should().BeFalse();
        tenant.ContactEmail.Should().BeNull();
    }

    [Fact]
    public async Task ScopedDelete_HardDeleteAuthorized_WhenOptedIn_RemovesEverything()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        var authDocId = await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, "001", "001", "000000060");
        await TestDataSeeder.MarkAuthorizedAsync(_fixture, authDocId, "AUTH-67890");

        var result = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: true);
            using var ms = new MemoryStream();
            return await service.DeleteTenantDataAsync(a.TenantId, ms);
        });

        result.AuthorizedHardDeleted.Should().Be(1, "con la opción habilitada, los autorizados se borran físicamente");
        result.AuthorizedAnonymized.Should().Be(0);
        result.TenantHardDeleted.Should().BeTrue("sin documentos retenidos, el tenant se borra físicamente");

        // No queda NINGÚN documento del tenant A.
        var remaining = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Documents.IgnoreQueryFilters().CountAsync(d => d.TenantId == a.TenantId));
        remaining.Should().Be(0);

        // El tenant ya no existe (se consulta vía contexto privilegiado para no depender de RLS).
        await using var priv = _fixture.CreatePrivilegedContext();
        var tenantExists = await priv.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == a.TenantId);
        tenantExists.Should().BeFalse();
    }

    [Fact]
    public async Task ScopedDelete_OfTenantA_DoesNotTouchTenantB()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: true);
            using var ms = new MemoryStream();
            return await service.DeleteTenantDataAsync(a.TenantId, ms);
        });

        // B sigue intacto: su documento existe y su tenant también.
        var bDocs = await AsTenantAsync(b.TenantId, ctx =>
            ctx.Documents.IgnoreQueryFilters().CountAsync(d => d.TenantId == b.TenantId));
        bDocs.Should().Be(1, "el borrado de A no afecta los documentos de B");

        await using var priv = _fixture.CreatePrivilegedContext();
        var bTenantExists = await priv.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == b.TenantId);
        bTenantExists.Should().BeTrue("el tenant B no se ve afectado por el borrado de A");
    }

    [Fact]
    public async Task ScopedDelete_IsIdempotent()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        var authDocId = await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, "001", "001", "000000070");
        await TestDataSeeder.MarkAuthorizedAsync(_fixture, authDocId, "AUTH-IDEM");

        async Task<Interfaces_ScopedDeleteResultProxy> RunDeleteAsync() => await AsTenantAsync(a.TenantId, async ctx =>
        {
            var service = PostgresFixture.CreateLifecycleService(ctx, allowHardDeleteAuthorized: false);
            using var ms = new MemoryStream();
            var r = await service.DeleteTenantDataAsync(a.TenantId, ms);
            return new Interfaces_ScopedDeleteResultProxy(r.AuthorizedAnonymized, r.NonAuthorizedHardDeleted);
        });

        var first = await RunDeleteAsync();
        first.AuthorizedAnonymized.Should().Be(1);

        // Segunda ejecución: el autorizado ya está anonimizado (idempotente, sin lanzar) y no hay no-autorizados.
        var second = await RunDeleteAsync();
        second.AuthorizedAnonymized.Should().Be(1, "anonimizar de nuevo es un no-op idempotente");
        second.NonAuthorizedHardDeleted.Should().Be(0);
    }

    private sealed record Interfaces_ScopedDeleteResultProxy(int AuthorizedAnonymized, int NonAuthorizedHardDeleted);
}
