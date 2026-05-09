using FluentAssertions;
using Moq;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.UnitTests.Application.Commands.Email;

public class ConfigureEmailSettingsCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private ConfigureEmailSettingsCommandHandler CreateHandler() =>
        new(_tenantRepo.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_ShouldUpdateTenantEmailSettings_AndSave()
    {
        // Arrange
        var tenant = Tenant.Create("1792268071001", "Test Corp");

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);

        var command = new ConfigureEmailSettingsCommand(
            tenant.Id,
            EmailEnabled: true,
            EmailProvider: EmailProvider.Custom,
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            SmtpUser: "user@example.com",
            SmtpPassword: "secret",
            UseSsl: true,
            SenderEmail: "billing@example.com",
            SenderName: "Example Billing");

        var handler = CreateHandler();

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert: tenant settings were updated
        tenant.EmailEnabled.Should().BeTrue();
        tenant.EmailProvider.Should().Be(EmailProvider.Custom);
        tenant.SmtpHost.Should().Be("smtp.example.com");
        tenant.SmtpPort.Should().Be(587);
        tenant.SmtpUser.Should().Be("user@example.com");
        tenant.SmtpPassword.Should().Be("secret");
        tenant.UseSsl.Should().BeTrue();
        tenant.SenderEmail.Should().Be("billing@example.com");
        tenant.SenderName.Should().Be("Example Billing");

        // Assert: repository and unit of work were called
        _tenantRepo.Verify(r => r.UpdateAsync(tenant, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTenantNotFound_ShouldThrow()
    {
        // Arrange
        _tenantRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        var command = new ConfigureEmailSettingsCommand(
            Guid.NewGuid(),
            EmailEnabled: false,
            EmailProvider: EmailProvider.Qora,
            null, null, null, null, true, null, null);

        var handler = CreateHandler();

        // Act
        var act = async () => await handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DisablingEmail_ShouldSetEmailEnabledFalse()
    {
        // Arrange
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.ConfigureEmail(emailEnabled: true, emailProvider: EmailProvider.Qora); // start enabled

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);

        var command = new ConfigureEmailSettingsCommand(
            tenant.Id,
            EmailEnabled: false,
            EmailProvider: EmailProvider.Qora,
            null, null, null, null, true, null, null);

        var handler = CreateHandler();

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        tenant.EmailEnabled.Should().BeFalse();
    }
}
