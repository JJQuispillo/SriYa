using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("El cuerpo de la solicitud es requerido.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.Ruc)
                .NotEmpty()
                .WithMessage("El RUC es requerido.")
                .Length(13)
                .WithMessage("El RUC debe tener exactamente 13 caracteres.")
                .Matches(@"^\d{13}$")
                .WithMessage("El RUC debe contener solo dígitos.");

            RuleFor(x => x.Request.BusinessName)
                .NotEmpty()
                .WithMessage("La razón social es requerida.")
                .MaximumLength(300)
                .WithMessage("La razón social no debe exceder los 300 caracteres.");
        });
    }
}
