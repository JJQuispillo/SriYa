using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyCommandValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required.");

        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("Request body is required.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.Name)
                .NotEmpty()
                .WithMessage("API key name is required.")
                .MaximumLength(100)
                .WithMessage("API key name must not exceed 100 characters.");

            RuleFor(x => x.Request.ExpiresAt)
                .GreaterThan(DateTime.UtcNow)
                .When(x => x.Request.ExpiresAt.HasValue)
                .WithMessage("Expiration date must be in the future.");
        });
    }
}
