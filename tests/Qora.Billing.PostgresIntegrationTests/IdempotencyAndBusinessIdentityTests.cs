using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// P2 sobre Postgres REAL:
///   II-1 (replay por Idempotency-Key): misma clave + mismo hash devuelve el registro original; clave +
///        hash distinto debe poder detectarse como conflicto (request_hash difiere).
///   II-2 (dedupe de identidad de negocio): el unique parcial ux_documents_business_identity existe y
///        bloquea un segundo comprobante con (tenant, tipo, estab, ptoEmi, secuencial) idéntico, sin un 500
///        opaco — se traduce a violación de unicidad detectable.
///   RLS: idempotency_keys está aislada por tenant igual que el resto de tablas (A no ve las claves de B).
///
/// Imposible de cubrir con el proveedor InMemory (no hay constraints reales, ni RLS, ni unique parcial).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdempotencyAndBusinessIdentityTests
{
    private readonly PostgresFixture _fixture;

    public IdempotencyAndBusinessIdentityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Corre una unidad de trabajo como billing_app con el tenant fijado, replicando el sobre
    /// transaccional + GUC de TenantContextMiddleware (igual que TenantIsolationTests.AsTenantAsync).
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

    // ── II-1: replay por Idempotency-Key ──────────────────────────────────────────

    [Fact]
    public async Task II1_Replay_SameKeySameHash_ReturnsOriginalStoredRecord()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string key = "replay-k1";
        var hash = $"hash-{Guid.NewGuid():N}";
        var docId = Guid.NewGuid();
        var snapshot = "{\"Id\":\"" + docId + "\",\"NumeroAutorizacion\":\"AUTH-ORIG\"}";

        // Primera emisión: TryStart (lock) + Complete con el snapshot.
        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            var entry = IdempotencyKey.Start(a.TenantId, key, hash, DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(entry)).Should().BeTrue();
            entry.Complete(snapshot, docId);
            await store.CompleteAsync(entry);
            return true;
        });

        // Reintento: misma clave + mismo hash → se recupera el registro completado original (sin reemitir).
        var replayed = await AsTenantAsync<IdempotencyKey?>(a.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            return await store.FindAsync(key);
        });

        Assert.NotNull(replayed);
        replayed.IsCompleted.Should().BeTrue();
        replayed.RequestHash.Should().Be(hash, "el mismo cuerpo produce el mismo hash → es un replay, no un conflicto");
        replayed.ResponseSnapshot.Should().Contain("AUTH-ORIG");
        replayed.DocumentId.Should().Be(docId);

        // Y sólo existe UN registro para esa (tenant, key): la segunda inserción habría colisionado.
        var count = await AsTenantAsync(a.TenantId, ctx =>
            ctx.IdempotencyKeys.CountAsync(k => k.Key == key));
        count.Should().Be(1);
    }

    [Fact]
    public async Task II1_SameKeyDifferentHash_IsDetectableAsConflict()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string key = "conflict-k1";
        var originalHash = $"hash-orig-{Guid.NewGuid():N}";

        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            var entry = IdempotencyKey.Start(a.TenantId, key, originalHash, DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(entry)).Should().BeTrue();
            return true;
        });

        // Reintento con un cuerpo distinto (otro hash). El registro persistido conserva el hash original,
        // por lo que el handler detecta el conflicto comparando hashes (→ 422). Aquí verificamos el dato.
        var existing = await AsTenantAsync<IdempotencyKey?>(a.TenantId, ctx =>
            ctx.IdempotencyKeys.FirstOrDefaultAsync(k => k.Key == key));

        Assert.NotNull(existing);
        existing.RequestHash.Should().Be(originalHash);
        var newHash = $"hash-distinto-{Guid.NewGuid():N}";
        existing.RequestHash.Should().NotBe(newHash, "clave reusada con cuerpo distinto → conflicto detectable");
    }

    [Fact]
    public async Task II1_SameKey_SecondInsert_CollidesOnUniqueConstraint()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string key = "lock-k1";

        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            var first = IdempotencyKey.Start(a.TenantId, key, "h1", DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(first)).Should().BeTrue("la primera inserción adquiere el lock");

            // Segunda inserción con la misma (tenant, key): el unique actúa como lock → false (no excepción).
            var second = IdempotencyKey.Start(a.TenantId, key, "h2", DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(second)).Should().BeFalse("una segunda clave igual colisiona con el unique");
            return true;
        });
    }

    // ── RLS: idempotency_keys aislada por tenant ───────────────────────────────────

    [Fact]
    public async Task RLS_IdempotencyKeys_AreTenantIsolated()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // A crea una clave.
        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            var entry = IdempotencyKey.Start(a.TenantId, "shared-key", "ha", DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(entry)).Should().BeTrue();
            return true;
        });

        // B no debe ver la clave de A — ni con el filtro de EF ni evadiéndolo (RLS).
        var bSeesViaStore = await AsTenantAsync(b.TenantId, ctx =>
            ctx.IdempotencyKeys.CountAsync(k => k.Key == "shared-key"));
        bSeesViaStore.Should().Be(0, "B no puede ver las claves de idempotencia de A");

        var bSeesIgnoringFilters = await AsTenantAsync(b.TenantId, ctx =>
            ctx.IdempotencyKeys.IgnoreQueryFilters().Where(k => k.TenantId == a.TenantId).CountAsync());
        bSeesIgnoringFilters.Should().Be(0, "RLS oculta las claves de A para B aun ignorando el filtro de EF");

        // B PUEDE reusar la misma cadena de clave de forma independiente (clave por-tenant).
        await AsTenantAsync(b.TenantId, async ctx =>
        {
            var store = new IdempotencyStore(ctx);
            var entry = IdempotencyKey.Start(b.TenantId, "shared-key", "hb", DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(entry)).Should().BeTrue("la misma cadena de clave en otro tenant es independiente");
            return true;
        });

        // RLS via SQL crudo: A con su GUC sólo cuenta su propia clave.
        await using var conn = _fixture.CreateRawAppConnection();
        await conn.OpenAsync();
        await using var rawTx = await conn.BeginTransactionAsync();
        await using (var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @t, true)", conn, (NpgsqlTransaction)rawTx))
        {
            setCmd.Parameters.AddWithValue("t", a.TenantId.ToString());
            await setCmd.ExecuteNonQueryAsync();
        }
        await using var countCmd = new NpgsqlCommand(
            "SELECT count(*) FROM idempotency_keys", conn, (NpgsqlTransaction)rawTx);
        var visible = (long)(await countCmd.ExecuteScalarAsync())!;
        visible.Should().Be(1, "A sólo ve su propia clave bajo RLS, nunca la de B");
        await rawTx.RollbackAsync();
    }

    // ── II-2: identidad de negocio (unique parcial + dedupe) ───────────────────────

    [Fact]
    public async Task II2_UniqueConstraint_Exists_OnDocumentsBusinessIdentity()
    {
        await using var conn = _fixture.CreateRawAppConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_documents_business_identity'", conn);
        var exists = (long)(await cmd.ExecuteScalarAsync())!;
        exists.Should().Be(1, "el unique constraint de identidad de negocio debe existir tras la migración B4");
    }

    [Fact]
    public async Task II2_DuplicateBusinessIdentity_ViolatesUniqueConstraint_NotA500()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Insertamos un comprobante con una identidad de negocio concreta para A.
        var issuer = new Dictionary<string, string>
        {
            ["estab"] = "002",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000777",
        };
        var buyer = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };

        await AsTenantAsync(a.TenantId, async ctx =>
        {
            var doc = Document.Create(a.TenantId, DocumentType.Factura, issuer, buyer);
            doc.SetXmlContent("<xml/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));
            await ctx.Documents.AddAsync(doc);
            await ctx.SaveChangesAsync();
            return true;
        });

        // Un segundo comprobante con la MISMA identidad debe chocar con el unique (II-2), capturable como
        // PostgresException 23505 / ConstraintName ux_documents_business_identity → el dominio lo deduplica.
        var act = async () => await AsTenantAsync(a.TenantId, async ctx =>
        {
            var dup = Document.Create(a.TenantId, DocumentType.Factura, issuer, buyer);
            dup.SetXmlContent("<xml/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));
            await ctx.Documents.AddAsync(dup);
            await ctx.SaveChangesAsync();
            return true;
        });

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var postgres = Assert.IsType<PostgresException>(ex.Which.InnerException);
        postgres.SqlState.Should().Be("23505", "una identidad de negocio duplicada viola el unique, no produce un 500 opaco");
        postgres.ConstraintName.Should().Be("ux_documents_business_identity");
    }

    [Fact]
    public async Task II2_DistinctSecuencial_Succeeds()
    {
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        var buyer = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };

        async Task InsertAsync(string secuencial) => await AsTenantAsync(a.TenantId, async ctx =>
        {
            var issuer = new Dictionary<string, string>
            {
                ["estab"] = "003",
                ["ptoEmi"] = "001",
                ["secuencial"] = secuencial,
            };
            var doc = Document.Create(a.TenantId, DocumentType.Factura, issuer, buyer);
            doc.SetXmlContent("<xml/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));
            await ctx.Documents.AddAsync(doc);
            await ctx.SaveChangesAsync();
            return true;
        });

        await InsertAsync("000000100");
        var act = async () => await InsertAsync("000000101");
        await act.Should().NotThrowAsync("un secuencial distinto es una identidad de negocio distinta");
    }

    [Fact]
    public async Task II2_DuplicateAcrossTenants_IsAllowed()
    {
        var (a, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        var buyer = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };
        var issuer = new Dictionary<string, string>
        {
            ["estab"] = "004",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000900",
        };

        async Task InsertForAsync(Guid tenantId) => await AsTenantAsync(tenantId, async ctx =>
        {
            var doc = Document.Create(tenantId, DocumentType.Factura, issuer, buyer);
            doc.SetXmlContent("<xml/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));
            await ctx.Documents.AddAsync(doc);
            await ctx.SaveChangesAsync();
            return true;
        });

        await InsertForAsync(a.TenantId);
        var act = async () => await InsertForAsync(b.TenantId);
        await act.Should().NotThrowAsync("la identidad de negocio está acotada por tenant: misma tupla en otro tenant es válida");
    }
}
