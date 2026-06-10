using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyCommandValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("El TenantId es requerido.");

        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("El cuerpo de la solicitud es requerido.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.Nombre)
                .NotEmpty()
                .WithMessage("El nombre de la API key es requerido.")
                .MaximumLength(100)
                .WithMessage("El nombre de la API key no debe exceder los 100 caracteres.");

            RuleFor(x => x.Request.FechaExpiracion)
                .GreaterThan(DateTime.UtcNow)
                .When(x => x.Request.FechaExpiracion.HasValue)
                .WithMessage("La fecha de expiración debe ser en el futuro.");
        });
    }
}
