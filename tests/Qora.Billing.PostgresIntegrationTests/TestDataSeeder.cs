using Microsoft.EntityFrameworkCore;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.PostgresIntegrationTests;

/// <summary>
/// Datos sembrados para un tenant en el suite de aislamiento. Se siembra a través del contexto
/// privilegiado (BYPASSRLS) porque las inserciones cross-tenant de la fase de arranque del test no
/// llevan GUC de tenant; bajo RLS normal serían bloqueadas.
/// </summary>
public sealed record SeededTenant(
    Guid TenantId,
    Guid DocumentId,
    string AccessKey,
    string ApiKeyHash);

/// <summary>
/// Siembra datos para dos tenants (A y B) usando el contexto privilegiado, incluyendo filas hijas
/// (items / destinatarios / items de destinatario) y una api_key por tenant.
/// </summary>
public static class TestDataSeeder
{
    public static async Task<(SeededTenant A, SeededTenant B)> SeedTwoTenantsAsync(PostgresFixture fixture)
    {
        await using var ctx = fixture.CreatePrivilegedContext();

        // RUCs únicos por llamada para que múltiples pruebas que comparten el mismo contenedor no choquen
        // contra el unique de RUC. El aislamiento de RLS hace que cada prueba sólo vea sus propios tenants,
        // por lo que las aserciones por-tenant siguen siendo deterministas.
        var a = await SeedTenantAsync(ctx, GenerateRuc(), "Empresa A", "estabA");
        var b = await SeedTenantAsync(ctx, GenerateRuc(), "Empresa B", "estabB");

        await ctx.SaveChangesAsync();

        return (a, b);
    }

    private static async Task<SeededTenant> SeedTenantAsync(
        BillingPrivilegedDbContext ctx,
        string ruc,
        string businessName,
        string apiKeyHashSeed)
    {
        var tenant = Tenant.Create(ruc, businessName);
        await ctx.Tenants.AddAsync(tenant);

        var issuerInfo = new Dictionary<string, string>
        {
            ["estab"] = "001",
            ["ptoEmi"] = "001",
            ["secuencial"] = "000000001",
        };
        var buyerInfo = new Dictionary<string, string> { ["razonSocialComprador"] = "Cliente" };

        var document = Document.Create(tenant.Id, DocumentType.Factura, issuerInfo, buyerInfo);

        // Las filas hijas se añaden mientras el documento está en Draft (la máquina de estados lo exige).
        document.AddItem(DocumentItem.Create(
            document.Id, "P001", $"Producto {businessName}", 1m, 10m, 0m, 12m, "2", "2"));

        var destinatario = Destinatario.Create(
            "0993456789001", $"Destinatario {businessName}", "Av. Siempre Viva", "Venta", "0993456789001");
        destinatario.AddItem(DestinatarioItem.Create("D001", $"Detalle {businessName}", 1m));
        document.AddDestinatario(destinatario);

        // Tras añadir las hijas, se fija el XML + clave de acceso (Draft → XmlGenerated).
        var accessKey = GenerateAccessKey();
        document.SetXmlContent("<xml/>", new AccessKey(accessKey));

        await ctx.Documents.AddAsync(document);

        var apiKeyHash = $"hash-{apiKeyHashSeed}-{Guid.NewGuid():N}";
        var apiKey = ApiKey.Create(tenant.Id, apiKeyHash, "default");
        await ctx.ApiKeys.AddAsync(apiKey);

        return new SeededTenant(tenant.Id, document.Id, accessKey, apiKeyHash);
    }

    /// <summary>
    /// Marca un documento como PendingRetry (con NextRetryAt en el pasado) vía SQL crudo en el contexto
    /// privilegiado, para el escenario del escaneo all-tenant del SriRetryService. Se usa SQL directo
    /// porque la máquina de estados del dominio no permite saltar Draft→PendingRetry y aquí sólo nos
    /// interesa el dato de prueba, no la transición.
    /// </summary>
    public static async Task MarkPendingRetryAsync(PostgresFixture fixture, Guid documentId)
    {
        await using var ctx = fixture.CreatePrivilegedContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE documents
            SET status = {nameof(DocumentStatus.PendingRetry)},
                next_retry_at = {DateTime.UtcNow.AddMinutes(-5)}
            WHERE id = {documentId}");
    }

    /// <summary>
    /// Marca un documento como Authorized (con número/fecha de autorización) vía SQL crudo en el contexto
    /// privilegiado, para los escenarios del ciclo de vida (PL-2). Se usa SQL directo porque la máquina de
    /// estados del dominio exige el camino Draft→…→SentToSri→Authorized y aquí sólo nos interesa el dato.
    /// </summary>
    public static async Task MarkAuthorizedAsync(PostgresFixture fixture, Guid documentId, string authNumber)
    {
        await using var ctx = fixture.CreatePrivilegedContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE documents
            SET status = {nameof(DocumentStatus.Authorized)},
                sri_authorization_number = {authNumber},
                sri_authorization_date = {DateTime.UtcNow},
                signed_xml_content = '<factura/>'
            WHERE id = {documentId}");
    }

    /// <summary>
    /// Inserta un documento adicional para un tenant (vía contexto privilegiado) con la identidad de
    /// negocio indicada, dejándolo en estado Draft. Devuelve su Id. Útil para sembrar varios documentos
    /// (autorizados/no autorizados) por tenant en los escenarios de ciclo de vida.
    /// </summary>
    public static async Task<Guid> SeedExtraDocumentAsync(
        PostgresFixture fixture, Guid tenantId, string estab, string ptoEmi, string secuencial)
    {
        await using var ctx = fixture.CreatePrivilegedContext();
        var issuer = new Dictionary<string, string>
        {
            ["estab"] = estab,
            ["ptoEmi"] = ptoEmi,
            ["secuencial"] = secuencial,
            ["razonSocial"] = "Emisor Test",
            ["ruc"] = "0993456789001",
        };
        var buyer = new Dictionary<string, string>
        {
            ["razonSocialComprador"] = "Cliente PII",
            ["identificacionComprador"] = "0993456789001",
            ["direccionComprador"] = "Av. Privada 123",
            ["emailComprador"] = "cliente@example.com",
        };

        var doc = Document.Create(tenantId, DocumentType.Factura, issuer, buyer);
        doc.AddItem(DocumentItem.Create(doc.Id, "P-X", "Producto X", 2m, 5m, 0m, 12m, "2", "2"));
        doc.SetXmlContent("<factura/>", new AccessKey(GenerateAccessKey()));
        await ctx.Documents.AddAsync(doc);
        await ctx.SaveChangesAsync();
        return doc.Id;
    }

    /// <summary>
    /// Genera un RUC válido y único: provincia "09" (Guayas) + 8 dígitos aleatorios + "001".
    /// </summary>
    public static string GenerateRuc()
    {
        var rnd = new Random();
        var middle = string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('0' + rnd.Next(0, 10))));
        return $"09{middle}001";
    }

    /// <summary>
    /// Genera una clave de acceso de 49 dígitos con dígito verificador Mod11 válido.
    /// </summary>
    public static string GenerateAccessKey()
    {
        var rnd = new Random();
        var digits = new char[49];
        for (var i = 0; i < 48; i++)
            digits[i] = (char)('0' + rnd.Next(0, 10));

        digits[48] = ComputeMod11CheckDigit(new string(digits, 0, 48));
        return new string(digits);
    }

    private static char ComputeMod11CheckDigit(string first48)
    {
        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = first48.Length - 1; i >= 0; i--)
        {
            var weightIndex = (first48.Length - 1 - i) % weights.Length;
            sum += (first48[i] - '0') * weights[weightIndex];
        }

        var remainder = sum % 11;
        var checkDigit = 11 - remainder;
        checkDigit = checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };
        return (char)('0' + checkDigit);
    }
}
