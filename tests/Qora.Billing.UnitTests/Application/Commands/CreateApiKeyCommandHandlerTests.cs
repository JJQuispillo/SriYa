using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.UnitTests.Application.Commands;

public class CreateApiKeyCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IApiKeyRepository> _apiKeyRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly IOptions<ApiKeySettings> _prodSettings = Options.Create(new ApiKeySettings { Environment = "Production" });
    private readonly IOptions<ApiKeySettings> _testSettings = Options.Create(new ApiKeySettings { Environment = "Test" });

    [Fact]
    public async Task Handle_ShouldReturnPlaintextKeyOnCreation()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKey k, CancellationToken _) => k);

        var handler = new CreateApiKeyCommandHandler(_tenantRepo.Object, _apiKeyRepo.Object, _unitOfWork.Object, _prodSettings);
        var command = new CreateApiKeyCommand(tenant.Id, new CreateApiKeyRequest("Production Key"));

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.NotNull(result);
        result.Key.Should().NotBeNullOrEmpty();
        result.Key.Should().StartWith("qora_live_");
        result.Name.Should().Be("Production Key");
        result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ShouldHashKeyWithSha256()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        string? storedHash = null;

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()))
            .Callback<ApiKey, CancellationToken>((k, _) => storedHash = k.KeyHash)
            .ReturnsAsync((ApiKey k, CancellationToken _) => k);

        var handler = new CreateApiKeyCommandHandler(_tenantRepo.Object, _apiKeyRepo.Object, _unitOfWork.Object, _prodSettings);
        var command = new CreateApiKeyCommand(tenant.Id, new CreateApiKeyRequest("Test Key"));

        var result = await handler.Handle(command, CancellationToken.None);

        // The stored hash should match SHA-256 of the plaintext key
        var expectedHash = CreateApiKeyCommandHandler.HashApiKey(result.Key!);
        storedHash.Should().Be(expectedHash);
    }

    [Fact]
    public void HashApiKey_ShouldProduceConsistentHashes()
    {
        var key = "qora_live_testkey123";
        var hash1 = CreateApiKeyCommandHandler.HashApiKey(key);
        var hash2 = CreateApiKeyCommandHandler.HashApiKey(key);

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(64); // SHA-256 produces 64 hex chars
    }

    [Fact]
    public void HashApiKey_DifferentKeysShouldProduceDifferentHashes()
    {
        var hash1 = CreateApiKeyCommandHandler.HashApiKey("qora_live_key1");
        var hash2 = CreateApiKeyCommandHandler.HashApiKey("qora_live_key2");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public async Task Handle_TestEnvironment_ShouldUseTestPrefix()
    {
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKey k, CancellationToken _) => k);

        var handler = new CreateApiKeyCommandHandler(_tenantRepo.Object, _apiKeyRepo.Object, _unitOfWork.Object, _testSettings);
        var command = new CreateApiKeyCommand(tenant.Id, new CreateApiKeyRequest("Test Key"));

        var result = await handler.Handle(command, CancellationToken.None);

        result.Key.Should().StartWith("qora_test_");
    }
}
