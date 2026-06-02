using FluentAssertions;
using Moq;
using Qora.Billing.Application.Queries;
using Qora.Billing.Application.Queries.Handlers;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.UnitTests.Application.Queries;

public class GetApiKeysByTenantQueryHandlerTests
{
    private readonly Mock<IApiKeyRepository> _apiKeyRepo = new();

    private GetApiKeysByTenantQueryHandler CreateHandler() => new(_apiKeyRepo.Object);

    [Fact]
    public async Task Handle_ShouldReturnPaginatedResultsCorrectly()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var apiKeys = new List<ApiKey>
        {
            ApiKey.Create(tenantId, "hash1", "Key 1"),
            ApiKey.Create(tenantId, "hash2", "Key 2"),
            ApiKey.Create(tenantId, "hash3", "Key 3")
        };

        _apiKeyRepo.Setup(r => r.GetByTenantIdAsync(tenantId, 1, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((apiKeys.Take(2).ToList().AsReadOnly() as IReadOnlyList<ApiKey>, 3));

        var handler = CreateHandler();
        var query = new GetApiKeysByTenantQuery(tenantId, Page: 1, PageSize: 2);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(2);
        result.Pagina.Should().Be(1);
        result.TamanoPagina.Should().Be(2);
        result.Total.Should().Be(3);
        result.TotalPaginas.Should().Be(2);
    }

    [Fact]
    public async Task Handle_ShouldReturnNullKeyInResponse()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var apiKeys = new List<ApiKey>
        {
            ApiKey.Create(tenantId, "sha256hashvalue", "Production Key")
        };

        _apiKeyRepo.Setup(r => r.GetByTenantIdAsync(tenantId, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((apiKeys.AsReadOnly() as IReadOnlyList<ApiKey>, 1));

        var handler = CreateHandler();
        var query = new GetApiKeysByTenantQuery(tenantId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].Clave.Should().BeNull();
        result.Items[0].Nombre.Should().Be("Production Key");
        result.Items[0].Activo.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenTenantHasNoKeys_ShouldReturnEmptyList()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        _apiKeyRepo.Setup(r => r.GetByTenantIdAsync(tenantId, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<ApiKey>().ToList().AsReadOnly() as IReadOnlyList<ApiKey>, 0));

        var handler = CreateHandler();
        var query = new GetApiKeysByTenantQuery(tenantId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.Total.Should().Be(0);
        result.TotalPaginas.Should().Be(0);
    }
}
