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
            // Cascade.Stop: evalúa una regla a la vez para no mostrar errores
            // contradictorios (p. ej. "solo dígitos" sobre un input que sí es solo dígitos
            // pero de longitud incorrecta). La regla de dígitos ya no codifica la longitud.
            RuleFor(x => x.Request.Ruc)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithMessage("El RUC es requerido.")
                .Matches(@"^\d+$")
                .WithMessage("El RUC debe contener solo dígitos.")
                .Length(13)
                .WithMessage("El RUC debe tener exactamente 13 dígitos.");

            RuleFor(x => x.Request.RazonSocial)
                .NotEmpty()
                .WithMessage("La razón social es requerida.")
                .MaximumLength(300)
                .WithMessage("La razón social no debe exceder los 300 caracteres.");
        });
    }
}
