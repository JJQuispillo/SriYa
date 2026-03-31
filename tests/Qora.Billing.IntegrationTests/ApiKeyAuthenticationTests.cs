using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Integration tests for the API key authentication flow:
/// create tenant -> create API key -> use API key to authenticate.
/// Validates that Issue 1 (hash encoding mismatch) is fixed.
/// </summary>
[Collection("Integration")]
public class ApiKeyAuthenticationTests
{
    private readonly BillingApiFactory _factory;

    public ApiKeyAuthenticationTests(BillingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateAndUseApiKey_ShouldAuthenticateSuccessfully()
    {
        // 1. Create a tenant via service token
        var serviceClient = _factory.CreateClient();
        serviceClient.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);

        var tenantRequest = new CreateTenantRequest("1710034065001", "API Key Test Corp");
        var tenantResponse = await serviceClient.PostAsJsonAsync("/api/v1/tenants", tenantRequest);
        tenantResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantResponse>();

        // 2. Create an API key for the tenant — need to seed the DB since
        //    API key creation requires an authenticated tenant context
        //    We use the service token + direct DB seeding approach
        string plaintextKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var handler = new Qora.Billing.Application.Commands.Handlers.CreateApiKeyCommandHandler(
                scope.ServiceProvider.GetRequiredService<Qora.Billing.Domain.Interfaces.ITenantRepository>(),
                scope.ServiceProvider.GetRequiredService<Qora.Billing.Domain.Interfaces.IApiKeyRepository>(),
                scope.ServiceProvider.GetRequiredService<Qora.Billing.Domain.Interfaces.IUnitOfWork>(),
                Microsoft.Extensions.Options.Options.Create(
                    new Qora.Billing.Application.Settings.ApiKeySettings { Environment = "Test" }));

            var result = await handler.Handle(
                new Qora.Billing.Application.Commands.CreateApiKeyCommand(
                    tenant!.Id,
                    new CreateApiKeyRequest("Test Auth Key")),
                CancellationToken.None);

            plaintextKey = result.Key!;
        }

        // 3. Use the API key to authenticate
        var apiKeyClient = _factory.CreateClient();
        apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", plaintextKey);

        // Try to access a protected endpoint — e.g., create another API key
        // This should succeed because the hash encoding now matches (Issue 1 fix)
        var createKeyRequest = new CreateApiKeyRequest("Second Key");
        var response = await apiKeyClient.PostAsJsonAsync("/api/v1/api-keys", createKeyRequest);

        // If hash encoding matches, the API key auth should succeed
        // and we should get Created or some valid response (not 401)
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "API key authentication should succeed when hash encoding matches between creation and validation");
    }

    [Fact]
    public async Task InvalidApiKey_ShouldReturn401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "qora_test_invalid_key_that_does_not_exist");

        var response = await client.PostAsJsonAsync("/api/v1/api-keys",
            new CreateApiKeyRequest("Should Fail"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmptyApiKey_ShouldReturn401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "");

        var response = await client.PostAsJsonAsync("/api/v1/api-keys",
            new CreateApiKeyRequest("Should Fail"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoAuthHeader_ShouldReturn401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/api-keys",
            new CreateApiKeyRequest("Should Fail"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
