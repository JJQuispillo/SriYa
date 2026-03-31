using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("Request body is required.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.Ruc)
                .NotEmpty()
                .WithMessage("RUC is required.")
                .Length(13)
                .WithMessage("RUC must be exactly 13 characters.")
                .Matches(@"^\d{13}$")
                .WithMessage("RUC must contain only digits.");

            RuleFor(x => x.Request.BusinessName)
                .NotEmpty()
                .WithMessage("Business name is required.")
                .MaximumLength(300)
                .WithMessage("Business name must not exceed 300 characters.");
        });
    }
}
