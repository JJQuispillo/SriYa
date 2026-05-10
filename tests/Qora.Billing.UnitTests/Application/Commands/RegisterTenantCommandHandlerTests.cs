using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.UnitTests.Application.Commands;

public class RegisterTenantCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IApiKeyRepository> _apiKeyRepo = new();
    private readonly Mock<ISubscriptionRepository> _subscriptionRepo = new();
    private readonly Mock<IPlanRepository> _planRepo = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<RegisterTenantCommandHandler>> _logger = new();

    private readonly IOptions<ApiKeySettings> _apiKeySettings =
        Options.Create(new ApiKeySettings { Environment = "Test" });

    private RegisterTenantCommandHandler CreateHandler() => new(
        _tenantRepo.Object,
        _apiKeyRepo.Object,
        _subscriptionRepo.Object,
        _planRepo.Object,
        _emailService.Object,
        _unitOfWork.Object,
        _apiKeySettings,
        _logger.Object);

    private static RegisterTenantCommand ValidCommand() =>
        new("1792268071001", "Empresa de Prueba S.A.", null, "contacto@empresa.com");

    private static Plan FreePlan() =>
        Plan.Create("Free", "free", 50, 0m);

    [Fact]
    public async Task Handle_ValidCommand_CreatesTenantAndSubscriptionAndApiKey()
    {
        // Arrange
        var plan = FreePlan();

        _tenantRepo
            .Setup(r => r.GetByRucAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        _planRepo
            .Setup(r => r.GetBySlugAsync("free", It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        _tenantRepo
            .Setup(r => r.CreateAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant t, CancellationToken _) => t);

        _subscriptionRepo
            .Setup(r => r.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription s, CancellationToken _) => s);

        _apiKeyRepo
            .Setup(r => r.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKey k, CancellationToken _) => k);

        var handler = CreateHandler();
        var command = ValidCommand();

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        result.TenantId.Should().NotBeEmpty();
        result.ApiKey.Should().NotBeNullOrEmpty();
        result.ApiKey.Should().StartWith("qora_test_");
        result.TrialEndsAt.Should().BeAfter(DateTime.UtcNow);
        result.Message.Should().Contain("registrado");

        _tenantRepo.Verify(r => r.CreateAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Once);
        _subscriptionRepo.Verify(r => r.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()), Times.Once);
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DuplicateRuc_ThrowsException()
    {
        // Arrange
        var existingTenant = Tenant.Create("1792268071001", "Tenant Existente S.A.");

        _tenantRepo
            .Setup(r => r.GetByRucAsync("1792268071001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTenant);

        var handler = CreateHandler();
        var command = ValidCommand();

        // Act
        var act = () => handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<BillingDomainException>()
            .WithMessage("*1792268071001*");
    }

    [Fact]
    public async Task Handle_FreePlanNotFound_ThrowsInvalidOperationException()
    {
        // Arrange: no tenant with that RUC, but no plans exist at all
        _tenantRepo
            .Setup(r => r.GetByRucAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        _planRepo
            .Setup(r => r.GetBySlugAsync("free", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Plan?)null);

        _planRepo
            .Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plan>().AsReadOnly());

        var handler = CreateHandler();
        var command = ValidCommand();

        // Act
        var act = () => handler.Handle(command, CancellationToken.None);

        // Assert: handler throws BillingDomainException when no plans are available
        await act.Should().ThrowAsync<BillingDomainException>()
            .WithMessage("*planes activos*");
    }
}
