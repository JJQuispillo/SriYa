using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Application.Commands.Handlers;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.UnitTests.Application.Commands.Email;

public class SendDocumentEmailCommandHandlerTests
{
    private readonly Mock<IDocumentRepository> _documentRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IEmailService> _emailService = new();

    private SendDocumentEmailCommandHandler CreateHandler() =>
        new(_documentRepo.Object, _tenantRepo.Object, _emailService.Object,
            NullLogger<SendDocumentEmailCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ShouldCallEmailService_AndReturnResult()
    {
        // Arrange
        var tenant = Tenant.Create("1792268071001", "Test Corp");
        tenant.ConfigureEmail(emailEnabled: true, emailProvider: EmailProvider.Qora);

        var document = Document.Create(
            tenant.Id,
            DocumentType.Factura,
            new Dictionary<string, string> { ["ruc"] = "1792268071001" },
            new Dictionary<string, string> { ["correo"] = "buyer@test.com" });

        _documentRepo.Setup(r => r.GetByIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);

        _tenantRepo.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);

        _emailService.Setup(s => s.SendDocumentEmailAsync(document, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = CreateHandler();
        var command = new SendDocumentEmailCommand(document.Id, tenant.Id);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _emailService.Verify(s => s.SendDocumentEmailAsync(document, tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenDocumentNotFound_ShouldThrow()
    {
        // Arrange
        _documentRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document?)null);

        var handler = CreateHandler();
        var command = new SendDocumentEmailCommand(Guid.NewGuid(), Guid.NewGuid());

        // Act
        var act = async () => await handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Handle_WhenDocumentBelongsToDifferentTenant_ShouldThrow()
    {
        // Arrange
        var correctTenantId = Guid.NewGuid();
        var wrongTenantId = Guid.NewGuid();

        var document = Document.Create(
            correctTenantId,
            DocumentType.Factura,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        _documentRepo.Setup(r => r.GetByIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);

        var handler = CreateHandler();
        var command = new SendDocumentEmailCommand(document.Id, wrongTenantId); // wrong tenant

        // Act
        var act = async () => await handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();
    }
}
