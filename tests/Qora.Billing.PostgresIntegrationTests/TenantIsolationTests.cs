using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// TI-2 (RLS defensa en profundidad) + TI-1 (fail-closed) sobre Postgres REAL.
///
/// Cada prueba ejercita el rol billing_app (NO BYPASSRLS) replicando el sobre transaccional + GUC que
/// abre TenantContextMiddleware. RLS es lo único que separa los tenants a nivel de BD; estas pruebas
/// confirman que el aislamiento es real (imposible con el proveedor InMemory).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests
{
    private readonly PostgresFixture _fixture;

    public TenantIsolationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Ejecuta una unidad de trabajo como billing_app con tenant fijado, replicando el sobre
    /// transaccional de TenantContextMiddleware (BeginTransaction + flag IsInTransaction → el
    /// interceptor antepone set_config('app.current_tenant', ..., true)).
    /// </summary>
    private async Task<T> AsTenantAsync<T>(Guid tenantId, Func<Qora.Billing.Infrastructure.Persistence.BillingDbContext, Task<T>> work)
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

    // ── TI-2: aislamiento de RLS ──────────────────────────────────────────────────

    [Fact]
    public async Task TI2_TenantSet_ReturnsOnlyOwnRows()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        var aDocs = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Documents.Select(d => d.TenantId).ToListAsync());

        aDocs.Should().NotBeEmpty();
        aDocs.Should().OnlyContain(t => t == a.TenantId);
        aDocs.Should().NotContain(b.TenantId);
    }

    [Fact]
    public async Task TI2_FilterBypass_IgnoreQueryFilters_StillBlockedByRls()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Tenant A intenta leer la fila de B EVADIENDO el filtro de EF (IgnoreQueryFilters).
        // RLS debe seguir devolviendo 0 filas de B.
        var bRowsVisibleToA = await AsTenantAsync(a.TenantId, ctx =>
            ctx.Documents.IgnoreQueryFilters().Where(d => d.TenantId == b.TenantId).CountAsync());

        bRowsVisibleToA.Should().Be(0, "RLS debe bloquear las filas de B aun cuando se ignora el filtro de EF");
    }

    [Fact]
    public async Task TI2_FilterBypass_RawSql_StillBlockedByRls()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // SQL crudo a través de la conexión billing_app, dentro de una transacción con la GUC de A.
        // RLS debe ocultar las filas de B incluso para SQL que no pasa por EF.
        await using var conn = _fixture.CreateRawAppConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @t, true)", conn, (NpgsqlTransaction)tx))
        {
            setCmd.Parameters.AddWithValue("t", a.TenantId.ToString());
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var countCmd = new NpgsqlCommand(
            "SELECT count(*) FROM documents WHERE tenant_id = @b", conn, (NpgsqlTransaction)tx);
        countCmd.Parameters.AddWithValue("b", b.TenantId);
        var visibleBRows = (long)(await countCmd.ExecuteScalarAsync())!;

        visibleBRows.Should().Be(0, "RLS debe bloquear SQL crudo que intenta leer filas de otro tenant");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task TI2_UpdateOfOtherTenantRow_AffectsZeroRows()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Tenant A intenta UPDATE sobre la fila de B vía SQL crudo. RLS (USING) la vuelve invisible →
        // 0 filas afectadas (no error, pero tampoco modificación cross-tenant).
        await using var conn = _fixture.CreateRawAppConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @t, true)", conn, (NpgsqlTransaction)tx))
        {
            setCmd.Parameters.AddWithValue("t", a.TenantId.ToString());
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var updateCmd = new NpgsqlCommand(
            "UPDATE documents SET error_message = 'tampered' WHERE id = @id", conn, (NpgsqlTransaction)tx);
        updateCmd.Parameters.AddWithValue("id", b.DocumentId);
        var affected = await updateCmd.ExecuteNonQueryAsync();

        affected.Should().Be(0, "RLS debe impedir que A modifique filas de B");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task TI2_ChildRows_AreIsolatedByTenant()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Items y destinatarios de B no deben ser visibles para A (RLS sobre tenant_id desnormalizado).
        var (aItems, aDest, aDestItems) = await AsTenantAsync(a.TenantId, async ctx =>
        {
            var items = await ctx.DocumentItems.IgnoreQueryFilters().CountAsync();
            var dest = await ctx.Destinatarios.IgnoreQueryFilters().CountAsync();
            var destItems = await ctx.DestinatarioItems.IgnoreQueryFilters().CountAsync();
            return (items, dest, destItems);
        });

        // A sólo sembró 1 de cada uno. Si viera los de B, los conteos serían 2.
        aItems.Should().Be(1, "los document_items de B no deben ser visibles para A");
        aDest.Should().Be(1, "los destinatarios de B no deben ser visibles para A");
        aDestItems.Should().Be(1, "los items de destinatario de B no deben ser visibles para A");
    }

    [Fact]
    public async Task TI2_TenantContext_IsTransactionLocal_NoPoolLeak()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Solicitud 1: tenant A, completa y cierra la transacción (la conexión vuelve al pool).
        var aCount = await AsTenantAsync(a.TenantId, ctx => ctx.Documents.CountAsync());
        aCount.Should().BeGreaterThan(0);

        // Solicitud 2: reutiliza el pool con tenant B. Debe ver SÓLO B, nunca A (la GUC de A no se filtró).
        var bDocs = await AsTenantAsync(b.TenantId, ctx =>
            ctx.Documents.Select(d => d.TenantId).ToListAsync());

        bDocs.Should().OnlyContain(t => t == b.TenantId);
        bDocs.Should().NotContain(a.TenantId);

        // Y una solicitud sin transacción / sin GUC tras el pool no debe heredar la GUC de B.
        // (cubierto explícitamente por el suite fail-closed; aquí basta con confirmar que B no ve A.)
    }

    // ── TI-1: fail-closed (sin tenant → 0 filas) ──────────────────────────────────

    [Fact]
    public async Task TI1_NoTenantSet_TenantScopedRead_ReturnsZeroRows()
    {
        await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // billing_app SIN tenant fijado y SIN transacción/GUC. RLS (current_setting NULL → comparación
        // falsa) + filtros fail-closed de EF deben devolver 0 filas, nunca "todas".
        var (ctx, _) = _fixture.CreateAppContext(tenantId: null);
        await using var _disp = ctx;

        var docs = await ctx.Documents.CountAsync();

        docs.Should().Be(0, "sin tenant en contexto las consultas con alcance de tenant deben devolver 0 filas");
    }

    [Fact]
    public async Task TI1_NoTenantSet_IgnoreQueryFilters_StillZeroRows_AtDbLayer()
    {
        await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Incluso evadiendo los filtros de EF, RLS bajo billing_app sin GUC devuelve 0 filas.
        var (ctx, _) = _fixture.CreateAppContext(tenantId: null);
        await using var _disp = ctx;

        var docs = await ctx.Documents.IgnoreQueryFilters().CountAsync();

        docs.Should().Be(0, "sin GUC de tenant RLS oculta todas las filas aun ignorando el filtro de EF");
    }
}
