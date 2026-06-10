using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// OB-1 (onboarding atómico) sobre Postgres REAL — la mitad de bootstrap-rollback de la tarea 6.8:
///   - Happy path: el bootstrap crea tenant + certificado + API key en una sola transacción, y la API key
///     devuelta (en claro, una sola vez) AUTENTICA de verdad una solicitud posterior (su hash localiza una
///     api_key activa cuyo TenantId es el del emisor recién creado — el camino real de auth-by-hash).
///   - Rollback: un certificado/contraseña inválidos revierten TODO — no queda ni tenant, ni certificado,
///     ni api_key persistidos (rollback total, sin emisor a medio crear).
///   - Aislamiento: el emisor creado queda aislado por RLS de otros tenants (consultando como billing_app
///     bajo la GUC del nuevo tenant sólo se ven SUS filas, nunca las de otro emisor).
///
/// El bootstrap corre sobre la conexión PRIVILEGIADA (BYPASSRLS) porque al iniciarse el emisor aún no
/// existe ni hay GUC de tenant; imposible de cubrir con el proveedor InMemory (no hay RLS real).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BootstrapTests
{
    private readonly PostgresFixture _fixture;

    public BootstrapTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ── OB-1: happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bootstrap_CreatesTenantCertificateAndApiKey_Atomically()
    {
        var (certData, password) = CreateTestCertificate();
        var ruc = TestDataSeeder.GenerateRuc();

        var service = _fixture.CreateBootstrapService();
        var input = new BootstrapTenantInput(
            ruc, "Emisor Onboarding", "Comercial XYZ", "owner@example.com",
            certData, password, "Juan Quispe", "clave-inicial");

        var result = await service.BootstrapAsync(input);

        result.TenantId.Should().NotBeEmpty();
        result.Ruc.Should().Be(ruc);
        result.RazonSocial.Should().Be("Emisor Onboarding");
        result.CertificadoId.Should().NotBeEmpty();
        result.ApiKeyId.Should().NotBeEmpty();
        result.ApiKey.Should().StartWith("qora_test_").And.NotBeNullOrWhiteSpace();

        // Las tres filas quedaron persistidas. El contexto privilegiado conserva los GLOBAL query filters
        // fail-closed de EF (que sin tenant fijado devuelven 0 filas); por eso, igual que los caminos
        // cross-tenant en producción, leemos con IgnoreQueryFilters (RLS no aplica: BYPASSRLS).
        await using var ctx = _fixture.CreatePrivilegedContext();
        (await ctx.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == result.TenantId)).Should().BeTrue();
        (await ctx.ElectronicSignatures.IgnoreQueryFilters().AnyAsync(s => s.TenantId == result.TenantId && s.IsActive)).Should().BeTrue();
        (await ctx.ApiKeys.IgnoreQueryFilters().AnyAsync(k => k.TenantId == result.TenantId && k.IsActive)).Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_ReturnedApiKey_AuthenticatesSubsequentRequest()
    {
        var (certData, password) = CreateTestCertificate();
        var ruc = TestDataSeeder.GenerateRuc();

        var service = _fixture.CreateBootstrapService();
        var input = new BootstrapTenantInput(
            ruc, "Emisor Auth", null, null, certData, password, "Owner", "bootstrap");

        var result = await service.BootstrapAsync(input);

        // Camino real de auth-by-hash: el hash SHA-256 de la clave en claro debe localizar una api_key
        // ACTIVA cuyo TenantId es el del emisor recién aprovisionado (lo que el middleware usa para fijar
        // el contexto del tenant en las solicitudes posteriores).
        var keyHash = CreateApiKeyCommandHandler.HashApiKey(result.ApiKey);

        await using var ctx = _fixture.CreatePrivilegedContext();
        var matched = await ctx.ApiKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash);

        // FA 8.9.0 resuelve .Should().NotBeNull() sobre un tipo de referencia a la sobrecarga de enum
        // (CS0453); usamos Assert.NotNull como en los demás suites.
        Assert.NotNull(matched);
        matched.IsActive.Should().BeTrue();
        matched.TenantId.Should().Be(result.TenantId);
    }

    // ── OB-1: rollback total ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bootstrap_InvalidCertificate_RollsBackEverything()
    {
        var ruc = TestDataSeeder.GenerateRuc();
        // Bytes que NO son un PKCS#12 válido → la validación del certificado falla antes/dentro de la tx.
        var garbageCert = Encoding.UTF8.GetBytes("esto no es un certificado p12 valido");

        var service = _fixture.CreateBootstrapService();
        var input = new BootstrapTenantInput(
            ruc, "Emisor Fallido", null, null, garbageCert, "cualquier-clave", "Owner", "bootstrap");

        var act = () => service.BootstrapAsync(input);

        await act.Should().ThrowAsync<BillingDomainException>();

        // Rollback total: NO debe quedar NADA persistido para ese RUC/owner. Usamos IgnoreQueryFilters
        // para que la aserción sea real (sin él, el filtro fail-closed devolvería 0 filas igualmente).
        await using var ctx = _fixture.CreatePrivilegedContext();
        var rucVo = new Ruc(ruc);
        (await ctx.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Ruc == rucVo)).Should().BeFalse();
        (await ctx.ElectronicSignatures.IgnoreQueryFilters().AnyAsync(s => s.OwnerName == "Owner")).Should().BeFalse();
    }

    [Fact]
    public async Task Bootstrap_WrongCertificatePassword_RollsBackEverything()
    {
        var (certData, _) = CreateTestCertificate();
        var ruc = TestDataSeeder.GenerateRuc();

        var service = _fixture.CreateBootstrapService();
        var input = new BootstrapTenantInput(
            ruc, "Emisor Clave Mala", null, null, certData, "ContraseñaIncorrecta", "OwnerBad", "bootstrap");

        var act = () => service.BootstrapAsync(input);

        await act.Should().ThrowAsync<BillingDomainException>();

        await using var ctx = _fixture.CreatePrivilegedContext();
        var rucVo = new Ruc(ruc);
        (await ctx.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Ruc == rucVo)).Should().BeFalse();
        (await ctx.ElectronicSignatures.IgnoreQueryFilters().AnyAsync(s => s.OwnerName == "OwnerBad")).Should().BeFalse();
    }

    // ── OB-1 + TI: el emisor creado queda aislado por RLS ───────────────────────────────

    [Fact]
    public async Task Bootstrap_CreatedEmisor_IsTenantIsolatedFromOtherTenants()
    {
        // Emisor A vía bootstrap.
        var (certData, password) = CreateTestCertificate();
        var service = _fixture.CreateBootstrapService();
        var resultA = await service.BootstrapAsync(new BootstrapTenantInput(
            TestDataSeeder.GenerateRuc(), "Emisor A", null, null, certData, password, "OwnerA", "bootstrap"));

        // Otro emisor B (sembrado por el seeder).
        var (_, b) = await TestDataSeeder.SeedTwoTenantsAsync(_fixture);

        // Consultando como billing_app bajo la GUC del emisor A, las tablas con alcance de tenant
        // (api_keys, electronic_signatures — protegidas por RLS) sólo muestran las filas del emisor A,
        // NUNCA las del emisor B. Forzamos IgnoreQueryFilters para que el aislamiento provenga de RLS en
        // la BD (no de los filtros de EF). La tabla tenants NO está bajo RLS (es la raíz), por eso el
        // aislamiento se comprueba sobre las tablas de datos por-tenant.
        var (ctx, session) = _fixture.CreateAppContext(resultA.TenantId);
        await using var _ = ctx;
        await using var tx = await ctx.Database.BeginTransactionAsync();
        session.SetInTransaction(true);
        await PostgresFixture.SetTenantGucAsync(ctx, resultA.TenantId);
        try
        {
            (await ctx.ApiKeys.IgnoreQueryFilters().CountAsync()).Should().Be(1, "RLS limita a las api_keys del emisor A");
            (await ctx.ApiKeys.IgnoreQueryFilters().AnyAsync(k => k.TenantId == resultA.TenantId)).Should().BeTrue();
            (await ctx.ApiKeys.IgnoreQueryFilters().AnyAsync(k => k.TenantId == b.TenantId)).Should().BeFalse();

            (await ctx.ElectronicSignatures.IgnoreQueryFilters().CountAsync()).Should().Be(1, "RLS limita a los certificados del emisor A");
            (await ctx.ElectronicSignatures.IgnoreQueryFilters().AnyAsync(s => s.TenantId == resultA.TenantId)).Should().BeTrue();

            await tx.CommitAsync();
        }
        finally
        {
            session.SetInTransaction(false);
        }
    }

    /// <summary>
    /// Genera un certificado autofirmado y lo exporta como PKCS#12 (.pfx) protegido por contraseña,
    /// igual que el helper de las pruebas del firmador XAdES. Un certificado real válido para el happy path.
    /// </summary>
    private static (byte[] CertData, string Password) CreateTestCertificate()
    {
        const string password = "TestPassword123!";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Qora Bootstrap Test, O=Qora, C=EC",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2));

        var pfxData = cert.Export(X509ContentType.Pfx, password);
        return (pfxData, password);
    }
}
