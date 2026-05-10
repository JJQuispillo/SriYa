using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class RegisterTenantCommandValidator : AbstractValidator<RegisterTenantCommand>
{
    public RegisterTenantCommandValidator()
    {
        RuleFor(x => x.Ruc)
            .NotEmpty()
            .WithMessage("El RUC es requerido.")
            .Length(13)
            .WithMessage("El RUC debe tener exactamente 13 caracteres.")
            .Matches(@"^\d{13}$")
            .WithMessage("El RUC debe contener solo dígitos.");

        RuleFor(x => x.BusinessName)
            .NotEmpty()
            .WithMessage("La razón social es requerida.")
            .MaximumLength(300)
            .WithMessage("La razón social no debe exceder los 300 caracteres.");

        When(x => !string.IsNullOrEmpty(x.TradeName), () =>
        {
            RuleFor(x => x.TradeName)
                .MaximumLength(300)
                .WithMessage("El nombre comercial no debe exceder los 300 caracteres.");
        });

        RuleFor(x => x.ContactEmail)
            .NotEmpty()
            .WithMessage("El correo de contacto es requerido.")
            .EmailAddress()
            .WithMessage("El correo de contacto no tiene un formato válido.");
    }
}
