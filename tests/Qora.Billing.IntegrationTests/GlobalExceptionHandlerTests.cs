using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Integration tests verifying that the global exception handler returns
/// proper RFC 7807 ProblemDetails for various error scenarios.
/// </summary>
[Collection("Integration")]
public class GlobalExceptionHandlerTests
{
    private readonly HttpClient _client;

    public GlobalExceptionHandlerTests(BillingApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateTenant_WithInvalidRuc_ShouldReturn400ProblemDetails()
    {
        _client.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);

        // "123" is an invalid RUC (must be 13 digits)
        var request = new CreateTenantRequest("123", "Invalid RUC Corp");

        var response = await _client.PostAsJsonAsync("/api/v1/tenants", request);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        problem!.Title.Should().NotBeNullOrEmpty();
        problem.Status.Should().BeOneOf(400, 422);
    }

    [Fact]
    public async Task GetTenant_NonExistent_ShouldReturn404()
    {
        _client.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);

        var nonExistentId = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/tenants/{nonExistentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTenant_WithEmptyBody_ShouldReturnErrorResponse()
    {
        _client.DefaultRequestHeaders.Add("X-Service-Token", BillingApiFactory.TestServiceToken);

        var response = await _client.PostAsJsonAsync<object?>("/api/v1/tenants", null);

        // Should not be 500 — should be a 4xx client error
        ((int)response.StatusCode).Should().BeInRange(400, 499);
    }
}
