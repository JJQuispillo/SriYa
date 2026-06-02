using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Qora.Billing.Application.Behaviors;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.UnitTests.Application.Behaviors;

public class ValidationBehaviorTests
{
    [Fact]
    public async Task Handle_NoValidators_ShouldCallNext()
    {
        var behavior = new ValidationBehavior<CreateTenantCommand, TenantResponse>(
            Enumerable.Empty<IValidator<CreateTenantCommand>>());

        var command = new CreateTenantCommand(new CreateTenantRequest("1792268071001", "Test"));

        var result = await behavior.Handle(
            command,
            () => Task.FromResult(new TenantResponse(Guid.NewGuid(), "1792268071001", "Test", null, true, DateTime.UtcNow)),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Test", result.BusinessName);
    }

    [Fact]
    public async Task Handle_ValidInput_ShouldCallNext()
    {
        var validator = new AlwaysValidValidator();
        var behavior = new ValidationBehavior<CreateTenantCommand, TenantResponse>([validator]);

        var command = new CreateTenantCommand(new CreateTenantRequest("1792268071001", "Test"));

        var result = await behavior.Handle(
            command,
            () => Task.FromResult(new TenantResponse(Guid.NewGuid(), "1792268071001", "Test", null, true, DateTime.UtcNow)),
            CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Handle_InvalidInput_ShouldThrowValidationException()
    {
        var validator = new AlwaysInvalidValidator();
        var behavior = new ValidationBehavior<CreateTenantCommand, TenantResponse>([validator]);

        var command = new CreateTenantCommand(new CreateTenantRequest("", ""));

        var act = () => behavior.Handle(
            command,
            () => Task.FromResult(new TenantResponse(Guid.NewGuid(), "", "", null, true, DateTime.UtcNow)),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ValidationException>(act);
        Assert.Contains(ex.Errors, e => e.ErrorMessage == "Name is required.");
    }

    [Fact]
    public async Task Handle_MultipleValidators_ShouldAggregateErrors()
    {
        var validator1 = new FailsWithMessage("Error 1");
        var validator2 = new FailsWithMessage("Error 2");

        var behavior = new ValidationBehavior<CreateTenantCommand, TenantResponse>(
            [validator1, validator2]);

        var command = new CreateTenantCommand(new CreateTenantRequest("", ""));

        var act = () => behavior.Handle(
            command,
            () => Task.FromResult(new TenantResponse(Guid.NewGuid(), "", "", null, true, DateTime.UtcNow)),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ValidationException>(act);
        // Each validator produces at least 1 failure, verify both validators contributed
        Assert.True(ex.Errors.Count() >= 2, $"Expected at least 2 errors, got {ex.Errors.Count()}");
    }

    // Concrete test validator implementations
    private class AlwaysValidValidator : AbstractValidator<CreateTenantCommand>
    {
        // No rules = always valid
    }

    private class AlwaysInvalidValidator : AbstractValidator<CreateTenantCommand>
    {
        public AlwaysInvalidValidator()
        {
            RuleFor(x => x.Request.BusinessName)
                .Must(_ => false)
                .WithMessage("Name is required.");
        }
    }

    private class FailsWithMessage : AbstractValidator<CreateTenantCommand>
    {
        public FailsWithMessage(string message)
        {
            RuleFor(x => x.Request.Ruc)
                .Must(_ => false)
                .WithMessage(message);
        }
    }
}
