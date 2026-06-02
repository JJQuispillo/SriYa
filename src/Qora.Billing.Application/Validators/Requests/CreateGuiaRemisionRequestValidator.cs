using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

public class CreateGuiaRemisionRequestValidator : AbstractValidator<CreateGuiaRemisionRequest>
{
    public CreateGuiaRemisionRequestValidator()
    {
        RuleFor(x => x.Emisor)
            .NotNull()
            .WithName("emisor")
            .WithMessage("La información del emisor es requerida.");

        RuleFor(x => x.Emisor.RazonSocial)
            .NotEmpty()
            .When(x => x.Emisor is not null)
            .WithName("emisor.razonSocial")
            .WithMessage("La información del emisor debe contener la razón social.");

        RuleFor(x => x.Emisor.Ruc)
            .Must(RequestValidationRules.IsValidRuc)
            .When(x => x.Emisor is not null)
            .WithName("emisor.ruc")
            .WithMessage("El RUC del emisor debe tener exactamente 13 dígitos.");

        RuleFor(x => x.Emisor.RucTransportista)
            .Must(RequestValidationRules.IsValidRuc)
            .When(x => x.Emisor is not null)
            .WithName("emisor.rucTransportista")
            .WithMessage("El RUC del transportista debe tener exactamente 13 dígitos.");

        RuleFor(x => x.Sustento)
            .NotNull()
            .WithName("sustento")
            .WithMessage("La información del documento de sustento es requerida.");

        RuleFor(x => x.Destinatarios)
            .NotNull()
            .Must(d => d != null && d.Count > 0)
            .WithName("destinatarios")
            .WithMessage("Se requiere al menos un destinatario.");
    }
}
