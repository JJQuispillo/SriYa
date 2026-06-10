using FluentValidation.TestHelper;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Validators;

namespace Qora.Billing.UnitTests.Application.Validators;

public class CreateTenantCommandValidatorTests
{
    private readonly CreateTenantCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_ShouldPass()
    {
        var command = new CreateTenantCommand(new CreateTenantRequest("1792268071001", "Test Corp"));
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyRuc_ShouldFail()
    {
        var command = new CreateTenantCommand(new CreateTenantRequest("", "Test Corp"));
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Request.Ruc);
    }

    [Fact]
    public void Validate_ShortRuc_ShouldFail()
    {
        var command = new CreateTenantCommand(new CreateTenantRequest("12345", "Test Corp"));
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Request.Ruc);
    }

    [Fact]
    public void Validate_NonDigitRuc_ShouldFail()
    {
        var command = new CreateTenantCommand(new CreateTenantRequest("179226807100X", "Test Corp"));
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Request.Ruc);
    }

    [Fact]
    public void Validate_EmptyBusinessName_ShouldFail()
    {
        var command = new CreateTenantCommand(new CreateTenantRequest("1792268071001", ""));
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Request.RazonSocial);
    }
}
