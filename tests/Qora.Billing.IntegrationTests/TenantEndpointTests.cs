using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Integration tests for tenant CRUD operations via the API.
/// </summary>
[Collection("Integration")]
public class TenantEndpointTests
{
    private readonly HttpClient _client;
    private readonly BillingApiFactory _factory;

    public TenantEndpointTests(BillingApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateTenant_WithServiceToken_ShouldReturn201()
    {
        _client.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);

        var request = new CreateTenantRequest("1791234567001", "Integration Test Corp", "ITC");

        var response = await _client.PostAsJsonAsync("/api/v1/tenants", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = await response.Content.ReadFromJsonAsync<TenantResponse>();
        Assert.NotNull(tenant);
        tenant!.Ruc.Should().Be("1791234567001");
        tenant.BusinessName.Should().Be("Integration Test Corp");
        tenant.TradeName.Should().Be("ITC");
        tenant.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTenant_WithoutAuth_ShouldReturn401()
    {
        var request = new CreateTenantRequest("1792268071001", "No Auth Corp");

        var response = await _client.PostAsJsonAsync("/api/v1/tenants", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateTenant_WithInvalidServiceToken_ShouldReturn401()
    {
        _client.DefaultRequestHeaders.Add("X-Service-Token", "wrong-token");

        var request = new CreateTenantRequest("1792268071001", "Bad Token Corp");

        var response = await _client.PostAsJsonAsync("/api/v1/tenants", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTenant_AfterCreation_ShouldReturnTenant()
    {
        // Create tenant first
        var createClient = _factory.CreateClient();
        createClient.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);
        var createRequest = new CreateTenantRequest("1793456789001", "Get Test Corp");
        var createResponse = await createClient.PostAsJsonAsync("/api/v1/tenants", createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdTenant = await createResponse.Content.ReadFromJsonAsync<TenantResponse>();

        // Get tenant using service token (no TenantId claim needed for ServiceToken auth)
        var getClient = _factory.CreateClient();
        getClient.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);
        var getResponse = await getClient.GetAsync($"/api/v1/tenants/{createdTenant!.Id}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tenant = await getResponse.Content.ReadFromJsonAsync<TenantResponse>();
        Assert.NotNull(tenant);
        tenant!.Id.Should().Be(createdTenant.Id);
        tenant.BusinessName.Should().Be("Get Test Corp");
    }

    [Fact]
    public async Task UpdateTenant_ShouldUpdateFields()
    {
        // Create
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);
        var createRequest = new CreateTenantRequest("1794567890001", "Original Name");
        var createResponse = await client.PostAsJsonAsync("/api/v1/tenants", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<TenantResponse>();

        // Update
        var updateClient = _factory.CreateClient();
        updateClient.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);
        var updateRequest = new UpdateTenantRequest("Updated Name", "New Trade");
        var updateResponse = await updateClient.PutAsJsonAsync($"/api/v1/tenants/{created!.Id}", updateRequest);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<TenantResponse>();
        updated!.BusinessName.Should().Be("Updated Name");
        updated.TradeName.Should().Be("New Trade");
    }
}
