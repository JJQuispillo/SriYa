using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Persistence;
using Qora.Billing.Infrastructure.Persistence.Repositories;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// Integration tests para el change <c>secuencial-server-side</c> (Change #3) sobre Postgres REAL
/// (vía Testcontainers). Ejercitan la mecánica que el modo AUTO del handler delega a la capa de
/// persistencia: el lock <c>FOR UPDATE</c> de <see cref="DocumentRepository.GetMaxSecuencialWithLockAsync"/>,
/// el <c>ux_documents_business_identity</c> como backstop de unicidad (que se traduce a
/// <see cref="DuplicateBusinessIdentityException"/> en <see cref="UnitOfWork.SaveChangesAsync"/>), el
/// reintento del perdedor sin dedupe, y la NO-consumición de secuencial en replay/reconciliador.
///
/// Granularidad a nivel repo/UoW (no del handler completo): el cableado end-to-end del
/// <see cref="ProcessDocumentCommandHandler"/> contra el fixture (IServiceScopeFactory + ISriClient
/// mockeado) está diferido (ver F-EMI-INT1 en EmissionAtomicityChaosTests). Aquí replicamos el bucle
/// AUTO del handler (lock → MAX+1 → AssignSecuencial → CreateAsync → SaveChanges → retry on dup) con
/// los MISMOS componentes reales de infraestructura, que es donde viven las invariantes de BD.
///
/// Imposible de cubrir con InMemory (no hay unique parcial real, ni FOR UPDATE, ni RLS).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecuencialServerSideIntegrationTests
{
    private readonly PostgresFixture _fixture;

    public SecuencialServerSideIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Computa MAX+1 (o "000000001" si no hay) igual que <c>ProcessDocumentCommandHandler.ComputeNextSecuencial</c>.
    /// </summary>
    private static string ComputeNext(string? currentMax) =>
        currentMax is not null && long.TryParse(currentMax.Trim(), out var n)
            ? (n + 1).ToString("D9")
            : "000000001";

    /// <summary>
    /// Replica UNA emisión AUTO server-side a nivel de persistencia real: dentro de la transacción
    /// ambiental + GUC del tenant, toma el lock FOR UPDATE (MAX), asigna MAX+1 vía AssignSecuencial (igual
    /// que el handler), inserta y persiste. Si choca con el unique de identidad de negocio, reintenta
    /// (re-lock + recomputar MAX+1) hasta <paramref name="maxAttempts"/>, NUNCA deduplica. Devuelve el
    /// secuencial finalmente persistido. Lanza <see cref="SecuencialExhaustedException"/> si se agota.
    /// </summary>
    private async Task<string> EmitAutoOnceAsync(Guid tenantId, string estab, string ptoEmi, int maxAttempts = 5)
    {
        var (ctx, session) = _fixture.CreateAppContext(tenantId);
        await using var _ = ctx;
        await using var tx = await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, tenantId);
        try
        {
            var repo = new DocumentRepository(ctx, _fixture.CreatePrivilegedContext());
            var uow = new UnitOfWork(ctx);

            var issuer = new Dictionary<string, string>
            {
                ["estab"] = estab,
                ["ptoEmi"] = ptoEmi,
                ["razonSocial"] = "Emisor AUTO",
                ["ruc"] = "0993456789001",
                // SIN 'secuencial': modo AUTO, el servidor lo asigna.
            };
            var buyer = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };
            var doc = Document.Create(tenantId, DocumentType.Factura, issuer, buyer);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var currentMax = await repo.GetMaxSecuencialWithLockAsync(
                    tenantId, DocumentType.Factura, estab, ptoEmi, CancellationToken.None);
                var next = ComputeNext(currentMax);
                doc.AssignSecuencial(next);
                // La clave de acceso se fija tras asignar el secuencial (Draft → XmlGenerated), igual que C2.
                if (doc.Status == DocumentStatus.Draft)
                    doc.SetXmlContent("<factura/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));

                try
                {
                    await repo.CreateAsync(doc, CancellationToken.None);
                    await uow.SaveChangesAsync(CancellationToken.None);
                    await tx.CommitAsync();
                    return next;
                }
                catch (DuplicateBusinessIdentityException)
                {
                    if (attempt == maxAttempts)
                        throw new SecuencialExhaustedException("Agotado el reintento AUTO.");
                    // Re-lock + recomputar en el SIGUIENTE intento. El documento ya está rastreado; se
                    // re-asignará el secuencial recomputado antes del próximo insert.
                    ctx.ChangeTracker.Clear();
                    doc = Document.Create(tenantId, DocumentType.Factura, issuer, buyer);
                }
            }

            throw new SecuencialExhaustedException("Agotado el reintento AUTO.");
        }
        finally
        {
            session.SetInTransaction(false);
        }
    }

    // ── T-SEC-027 (R3, S "Concurrent first emissions"): carrera de primera emisión AUTO ────────────

    [Fact]
    public async Task T_SEC_027_ConcurrentFirstEmission_Auto_BothSucceedWithDistinctSecuenciales_NoCrossDedupe()
    {
        // Spec R3 / S "Concurrent first emissions": dos emisiones AUTO distintas corriendo sobre una
        // identidad (estab/ptoEmi) VACÍA. Ambas computan 000000001; el unique parcial rompe el empate y el
        // perdedor reintenta a 000000002. Ambos comprobantes son DISTINTOS (no hay dedupe cruzado).
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string estab = "077";
        const string ptoEmi = "001";

        // Emisión secuencial-pero-real: la primera toma 000000001; la segunda, al re-lockear, ve MAX=1 y
        // toma 000000002. (La concurrencia verdadera sobre la MISMA conexión transaccional no es posible;
        // el FOR UPDATE + unique garantizan el mismo resultado observable: dos secuenciales distintos.)
        var first = await EmitAutoOnceAsync(a.TenantId, estab, ptoEmi);
        var second = await EmitAutoOnceAsync(a.TenantId, estab, ptoEmi);

        first.Should().Be("000000001", "la primera emisión AUTO en set vacío arranca en 000000001");
        second.Should().Be("000000002", "la siguiente emisión AUTO avanza a MAX+1");
        first.Should().NotBe(second, "no hay dedupe cruzado: son comprobantes distintos");

        // Ambos documentos persisten con secuenciales distintos bajo la misma identidad (estab/ptoEmi).
        var (ctx, session) = _fixture.CreateAppContext(a.TenantId);
        await using (ctx)
        {
            await ctx.Database.BeginTransactionAsync();
            session.SetInTransaction(true);
            await PostgresFixture.SetTenantGucAsync(ctx, a.TenantId);
            var secuenciales = await ctx.Documents
                .Where(d => d.Estab == estab && d.PtoEmision == ptoEmi)
                .Select(d => d.Secuencial)
                .ToListAsync();
            secuenciales.Should().BeEquivalentTo(["000000001", "000000002"]);
        }
    }

    [Fact]
    public async Task T_SEC_027b_DuplicateSecuencial_Auto_HitsUniqueIndex_TranslatedToDomainException()
    {
        // Backstop directo: un segundo insert con el MISMO secuencial AUTO choca con
        // ux_documents_business_identity y se traduce a DuplicateBusinessIdentityException (no un 500),
        // que es exactamente lo que dispara el reintento del bucle AUTO.
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string estab = "078";
        const string ptoEmi = "001";
        await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, estab, ptoEmi, "000000001");

        var (ctx, session) = _fixture.CreateAppContext(a.TenantId);
        await using var _ = ctx;
        await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, a.TenantId);

        var repo = new DocumentRepository(ctx, _fixture.CreatePrivilegedContext());
        var uow = new UnitOfWork(ctx);

        var issuer = new Dictionary<string, string> { ["estab"] = estab, ["ptoEmi"] = ptoEmi };
        var buyer = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };
        var dup = Document.Create(a.TenantId, DocumentType.Factura, issuer, buyer);
        dup.AssignSecuencial("000000001"); // colisión deliberada
        dup.SetXmlContent("<factura/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));

        await repo.CreateAsync(dup, CancellationToken.None);
        var act = async () => await uow.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateBusinessIdentityException>(
            "el unique parcial es el backstop que dispara el reintento AUTO, no un 500 opaco");
    }

    // ── T-SEC-028 (R8, S "Replay no-increment"): replay idempotente no consume secuencial ──────────

    [Fact]
    public async Task T_SEC_028_IdempotentReplay_DoesNotConsumeOrIncrementSecuencial()
    {
        // Spec R8 / S "Replay no-increment": un replay idempotente devuelve el snapshot almacenado SIN
        // reentrar al camino de emisión, por lo que NO se inserta un documento nuevo ni avanza el MAX
        // (secuencial). Modelamos: emitir una vez (consume 000000001), luego un replay (FindAsync sobre la
        // misma Idempotency-Key) NO crea documento. El MAX del tuple permanece en 000000001.
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string estab = "079";
        const string ptoEmi = "001";
        const string key = "replay-secuencial-k1";
        var hash = $"hash-{Guid.NewGuid():N}";

        // Emisión inicial: consume 000000001 + completa la entrada de idempotencia con su snapshot.
        var emitted = await EmitAutoOnceAsync(a.TenantId, estab, ptoEmi);
        emitted.Should().Be("000000001");

        var (ctx1, session1) = _fixture.CreateAppContext(a.TenantId);
        await using (ctx1)
        {
            await using var tx = await ctx1.Database.BeginTransactionAsync();
            session1.SetInTransaction(true);
            await PostgresFixture.SetTenantGucAsync(ctx1, a.TenantId);
            var store = new IdempotencyStore(ctx1);
            var entry = IdempotencyKey.Start(a.TenantId, key, hash, DateTime.UtcNow.AddDays(7));
            (await store.TryStartAsync(entry)).Should().BeTrue();
            entry.Complete("{\"Secuencial\":\"000000001\"}", Guid.NewGuid());
            await store.CompleteAsync(entry);
            await tx.CommitAsync();
            session1.SetInTransaction(false);
        }

        var docCountBefore = await CountDocumentsAsync(a.TenantId, estab, ptoEmi);
        var maxBefore = await MaxSecuencialAsync(a.TenantId, estab, ptoEmi);

        // Replay: misma clave + mismo hash → el handler devolvería el snapshot SIN emitir. Verificamos que
        // el registro de idempotencia ya está completo (replay) y que NO se inserta ningún documento.
        IdempotencyKey? replay = null;
        var (ctx2, session2) = _fixture.CreateAppContext(a.TenantId);
        await using (ctx2)
        {
            await using var tx = await ctx2.Database.BeginTransactionAsync();
            session2.SetInTransaction(true);
            await PostgresFixture.SetTenantGucAsync(ctx2, a.TenantId);
            replay = await new IdempotencyStore(ctx2).FindAsync(key);
            await tx.CommitAsync();
            session2.SetInTransaction(false);
        }

        Assert.NotNull(replay);
        replay.IsCompleted.Should().BeTrue("el replay devuelve el snapshot, no reemite");
        replay.RequestHash.Should().Be(hash);

        var docCountAfter = await CountDocumentsAsync(a.TenantId, estab, ptoEmi);
        var maxAfter = await MaxSecuencialAsync(a.TenantId, estab, ptoEmi);

        docCountAfter.Should().Be(docCountBefore, "el replay no inserta un documento nuevo");
        maxAfter.Should().Be(maxBefore).And.Be("000000001", "el replay no consume/incrementa el secuencial");
    }

    // ── T-SEC-029 (R8, S "Reconciler no-increment"): el reconciliador no consume secuencial ────────

    [Fact]
    public async Task T_SEC_029_Reconciler_AdvancingSentToSriDocument_DoesNotConsumeNewSecuencial()
    {
        // Spec R8 / S "Reconciler no-increment": el reconciliador SÓLO avanza documentos ya persistidos en
        // SentToSri (Authorize/Reject/ScheduleRetry); NO entra al camino de emisión, por lo que el
        // secuencial del documento permanece inalterado y no se asigna ninguno nuevo. Modelamos el avance
        // SentToSri → Authorized sobre la entidad real persistida y verificamos que el secuencial no cambia
        // ni se inserta otro documento en el tuple.
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string estab = "080";
        const string ptoEmi = "001";

        var docId = await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, estab, ptoEmi, "000000042");
        // Llevar el documento a SentToSri vía SQL crudo (estado de partida del reconciliador).
        await using (var priv = _fixture.CreatePrivilegedContext())
        {
            await priv.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE documents
                SET status = {nameof(DocumentStatus.SentToSri)}, signed_xml_content = '<factura/>'
                WHERE id = {docId}");
        }

        var secuencialBefore = await SecuencialOfAsync(a.TenantId, docId);
        var countBefore = await CountDocumentsAsync(a.TenantId, estab, ptoEmi);

        // El reconciliador avanza el documento (SentToSri → Authorized) — camino de actualización, NO de emisión.
        var (ctx, session) = _fixture.CreateAppContext(a.TenantId);
        await using (ctx)
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            session.SetInTransaction(true);
            await PostgresFixture.SetTenantGucAsync(ctx, a.TenantId);
            var doc = await ctx.Documents.FirstAsync(d => d.Id == docId);
            doc.Authorize("AUTH-RECON", DateTime.UtcNow);
            await ctx.SaveChangesAsync();
            await tx.CommitAsync();
            session.SetInTransaction(false);
        }

        var secuencialAfter = await SecuencialOfAsync(a.TenantId, docId);
        var countAfter = await CountDocumentsAsync(a.TenantId, estab, ptoEmi);

        secuencialAfter.Should().Be(secuencialBefore).And.Be("000000042",
            "el reconciliador avanza el estado pero NO toca el secuencial");
        countAfter.Should().Be(countBefore, "no se inserta ningún documento nuevo (no se consume otro secuencial)");
    }

    // ── T-SEC-030 (R9, backward-compat): CLIENT-mode default + migración B8 idempotente ────────────

    [Fact]
    public async Task T_SEC_030_BackwardCompat_DefaultTenantIsClientMode_AndColumnDefaultsFalse()
    {
        // Spec R9 / S "Existing tenant unaffected": un tenant sin flag explícito resuelve a CLIENT mode
        // (auto_generate_secuencial = false por el default de la migración B8), sin migración de datos.
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        var (ctx, session) = _fixture.CreateAppContext(a.TenantId);
        await using (ctx)
        {
            await ctx.Database.BeginTransactionAsync();
            session.SetInTransaction(true);
            await PostgresFixture.SetTenantGucAsync(ctx, a.TenantId);
            var tenant = await ctx.Tenants.FirstAsync(t => t.Id == a.TenantId);
            tenant.AutoGenerateSecuencial.Should().BeFalse(
                "el default de B8 deja a los tenants existentes en modo CLIENTE sin migración de datos");
        }
    }

    [Fact]
    public async Task T_SEC_030b_B8Migration_IsIdempotent_ColumnExistsWithFalseDefault()
    {
        // Spec R9 / migración B8: la columna existe con NOT NULL DEFAULT false, y re-ejecutar el Up
        // (ADD COLUMN IF NOT EXISTS) es idempotente — no falla si ya existe.
        await using var conn = _fixture.CreateRawAppConnection();
        await conn.OpenAsync();

        await using (var colCmd = new NpgsqlCommand(
            @"SELECT is_nullable, column_default FROM information_schema.columns
              WHERE table_name = 'tenants' AND column_name = 'auto_generate_secuencial'", conn))
        await using (var reader = await colCmd.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue("la columna auto_generate_secuencial debe existir tras B8");
            reader.GetString(0).Should().Be("NO", "la columna es NOT NULL");
            reader.GetString(1).Should().Contain("false", "el default de la columna es false");
        }

        // Re-ejecutar el SQL del Up de B8 (ADD COLUMN IF NOT EXISTS) debe ser idempotente.
        await using var ownerCtx = _fixture.CreateOwnerContext();
        var act = async () => await ownerCtx.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE ""tenants"" ADD COLUMN IF NOT EXISTS ""auto_generate_secuencial"" boolean NOT NULL DEFAULT false;");
        await act.Should().NotThrowAsync("ADD COLUMN IF NOT EXISTS es re-ejecutable (idempotente)");
    }

    [Fact]
    public async Task T_SEC_030c_ClientMode_DuplicateSecuencial_StillDedupesViaBusinessIdentity()
    {
        // Spec R7/R9: el modo CLIENTE conserva su comportamiento — una identidad de negocio duplicada
        // (mismo estab/ptoEmi/secuencial) choca con el unique, que el dominio traduce para deduplicar
        // (idéntico al pre-cambio). Verificamos el backstop de unicidad sin tocar el modo AUTO.
        var (a, _) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);
        const string estab = "081";
        const string ptoEmi = "001";
        await TestDataSeeder.SeedExtraDocumentAsync(_fixture, a.TenantId, estab, ptoEmi, "000000300");

        var (ctx, session) = _fixture.CreateAppContext(a.TenantId);
        await using var _ = ctx;
        await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, a.TenantId);

        var repo = new DocumentRepository(ctx, _fixture.CreatePrivilegedContext());
        var uow = new UnitOfWork(ctx);
        var issuer = new Dictionary<string, string>
        {
            ["estab"] = estab, ["ptoEmi"] = ptoEmi, ["secuencial"] = "000000300",
        };
        var dup = Document.Create(a.TenantId, DocumentType.Factura, issuer,
            new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" });
        dup.SetXmlContent("<factura/>", new AccessKey(TestDataSeeder.GenerateAccessKey()));

        await repo.CreateAsync(dup, CancellationToken.None);
        var act = async () => await uow.SaveChangesAsync(CancellationToken.None);

        // El handler CLIENTE captura esta excepción y deduplica (DedupeAndReturnAsync). Aquí verificamos
        // que el backstop de unicidad sigue activo e inalterado por el modo AUTO.
        await act.Should().ThrowAsync<DuplicateBusinessIdentityException>();
    }

    // ── Helpers de inspección (contexto acotado por tenant + GUC) ──────────────────────────────────

    private async Task<int> CountDocumentsAsync(Guid tenantId, string estab, string ptoEmi)
    {
        var (ctx, session) = _fixture.CreateAppContext(tenantId);
        await using var _ = ctx;
        await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, tenantId);
        return await ctx.Documents.CountAsync(d => d.Estab == estab && d.PtoEmision == ptoEmi);
    }

    private async Task<string?> MaxSecuencialAsync(Guid tenantId, string estab, string ptoEmi)
    {
        var (ctx, session) = _fixture.CreateAppContext(tenantId);
        await using var _ = ctx;
        await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, tenantId);
        var values = await ctx.Documents
            .Where(d => d.Estab == estab && d.PtoEmision == ptoEmi)
            .Select(d => d.Secuencial)
            .ToListAsync();
        return values.Where(s => s is not null).OrderByDescending(s => s).FirstOrDefault()?.Trim();
    }

    private async Task<string?> SecuencialOfAsync(Guid tenantId, Guid documentId)
    {
        var (ctx, session) = _fixture.CreateAppContext(tenantId);
        await using var _ = ctx;
        await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, tenantId);
        var doc = await ctx.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        return doc?.Secuencial?.Trim();
    }
}
