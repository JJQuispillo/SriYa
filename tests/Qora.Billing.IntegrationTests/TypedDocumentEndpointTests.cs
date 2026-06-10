using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.DTOs.Requests;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Integration tests for the 6 typed per-type document endpoints (WebApplicationFactory).
/// Verifies each typed route is reachable, validates its typed schema, and that the old
/// POST /api/v1/documents/process route no longer exists (404).
/// </summary>
[Collection("Integration")]
public class TypedDocumentEndpointTests
{
    private readonly BillingApiFactory _factory;

    public TypedDocumentEndpointTests(BillingApiFactory factory)
    {
        _factory = factory;
    }

    // ----- Helpers -----

    private async Task<(HttpClient Client, Guid TenantId)> CreateAuthenticatedTenantClientAsync(string ruc)
    {
        // 1. Create a tenant via service token.
        var serviceClient = _factory.CreateClient();
        serviceClient.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);
        var tenantResponse = await serviceClient.PostAsJsonAsync(
            "/api/v1/tenants", new CreateTenantRequest(ruc, "Documents Test Corp"));
        tenantResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantResponse>();

        // 2. Issue an API key for the tenant (direct handler, as in ApiKeyAuthenticationTests).
        string plaintextKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var handler = new Qora.Billing.Application.Commands.Handlers.CreateApiKeyCommandHandler(
                scope.ServiceProvider.GetRequiredService<ITenantRepository>(),
                scope.ServiceProvider.GetRequiredService<IApiKeyRepository>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                Microsoft.Extensions.Options.Options.Create(
                    new Qora.Billing.Application.Settings.ApiKeySettings { Environment = "Test" }));

            var result = await handler.Handle(
                new Qora.Billing.Application.Commands.CreateApiKeyCommand(
                    tenant!.Id, new CreateApiKeyRequest("Docs Test Key")),
                CancellationToken.None);
            plaintextKey = result.Clave!;
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", plaintextKey);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant!.Id.ToString());
        return (client, tenant.Id);
    }

    private async Task SeedActiveCertificateAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IElectronicSignatureRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var signature = ElectronicSignature.Create(
            tenantId,
            certificateData: [1, 2, 3, 4],
            passwordEncrypted: "encrypted-password",
            ownerName: "Documents Test Corp",
            expiresAt: DateTime.UtcNow.AddYears(1));
        await repo.CreateAsync(signature, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
    }

    private void ConfigureSuccessfulPipeline(DocumentType documentType)
    {
        const string signedXml =
            "<factura><infoTributaria><claveAcceso>1234567890123456789012345678901234567890123456789</claveAcceso></infoTributaria></factura>";

        _factory.DocumentTypeStrategyMock.SetupGet(s => s.DocumentType).Returns(documentType);
        _factory.DocumentTypeStrategyMock
            .Setup(s => s.ValidateDocumentAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _factory.DocumentTypeStrategyMock
            .Setup(s => s.BuildXmlAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildXmlResult(
                signedXml,
                new Qora.Billing.Domain.ValueObjects.AccessKey("1234567890123456789012345678901234567890123456789")));
        _factory.DocumentTypeStrategyMock
            .Setup(s => s.BuildRidePdfAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("%PDF-1.4 test"));

        _factory.DocumentSignerMock
            .Setup(d => d.SignDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signedXml);

        _factory.SriClientMock
            .Setup(c => c.SendDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Interfaces.SriSendResult(true, "RECIBIDA", Array.Empty<string>()));
        _factory.SriClientMock
            .Setup(c => c.CheckAuthorizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Interfaces.SriAuthorizationResult(
                true, "AUT-123", DateTime.UtcNow, "AUTORIZADO", Array.Empty<string>()));
    }

    private static EmisorBaseDto EmisorBase() =>
        new("1792268071001", "Documents Test Corp", "Av. Quito 123", "001", "001", "000000001");

    private static CompradorDto Comprador() =>
        new("04", "Buyer Corp", "0102030405001");

    private static ItemDto Item() =>
        new("PROD001", "Producto", 1, 50m, 0m, "2", "4");

    // ----- Happy path (Factura) -----

    [Fact]
    public async Task PostFactura_WithValidPayload_ShouldReturn201WithDocumentResponse()
    {
        var (client, tenantId) = await CreateAuthenticatedTenantClientAsync("1710034065001");
        await SeedActiveCertificateAsync(tenantId);
        ConfigureSuccessfulPipeline(DocumentType.Factura);

        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/facturas", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(document);
        document!.TipoDocumento.Should().Be(DocumentType.Factura);
    }

    // ----- Validation (422) per typed endpoint -----

    [Fact]
    public async Task PostFactura_WithInvalidRuc_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065002");
        var emisor = EmisorBase() with { Ruc = "123" };
        var request = new CreateFacturaRequest(emisor, Comprador(), [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/facturas", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostLiquidacionCompra_WithInvalidProveedorIdType_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065003");
        var proveedor = new ProveedorDto("99", "Proveedor SA", "1790012345001", "Av. Proveedor 1");
        var request = new CreateLiquidacionCompraRequest(EmisorBase(), proveedor, [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/liquidaciones-compra", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostNotaCredito_WithInvalidCodDocModificado_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065004");
        var sustento = new DocSustentoModificacionDto("99", "001-001-000000123", new DateTime(2026, 3, 9), "Devolución");
        var request = new CreateNotaCreditoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/notas-credito", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostNotaDebito_WithMissingNumDocSustento_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065005");
        var sustento = new DocSustentoDto("01", "", new DateTime(2026, 12, 1));
        var request = new CreateNotaDebitoRequest(EmisorBase(), Comprador(), sustento, [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/notas-debito", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostGuiaRemision_WithoutDestinatarios_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065006");
        var emisor = new EmisorGuiaDto(
            "1792268071001", "Documents Test Corp", "Av. Quito 123", "001", "001", "000000001",
            "Transportes SA", "1790012345001", "SI", "09/03/2026", "10/03/2026", "PBA1234");
        var request = new CreateGuiaRemisionRequest(emisor, new GuiaSustentoDto("01", "001-001-000000123"), []);

        var response = await client.PostAsJsonAsync("/api/v1/documents/guias-remision", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostRetencion_WithInvalidPeriodoFiscal_ShouldReturn422()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065007");
        var emisor = new EmisorRetencionDto(
            "1792268071001", "Documents Test Corp", "Av. Quito 123", "001", "001", "000000001", "2026-03");
        var item = new RetencionItemDto(
            "RET001", "Retención Renta", 1, 100m, 0m, "1", "303",
            "01", "001-001-000000123", new DateTime(2026, 2, 28), "1234567890");
        var request = new CreateComprobanteRetencionRequest(emisor, Comprador(), [item]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/retenciones", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ----- Old contract removed -----

    [Fact]
    public async Task PostProcess_OldEndpoint_ShouldReturn404()
    {
        var (client, _) = await CreateAuthenticatedTenantClientAsync("1710034065008");
        var request = new CreateFacturaRequest(EmisorBase(), Comprador(), [Item()]);

        var response = await client.PostAsJsonAsync("/api/v1/documents/process", request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
