using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

public class CreateFacturaRequestValidator : AbstractValidator<CreateFacturaRequest>
{
    public CreateFacturaRequestValidator()
    {
        RequestValidationRules.ApplyEmisorBaseRules(this, x => x.Emisor);

        RuleFor(x => x.Comprador)
            .NotNull()
            .WithName("comprador")
            .WithMessage("La información del comprador es requerida.");

        RuleFor(x => x.Detalles)
            .NotNull()
            .Must(items => items != null && items.Count > 0)
            .WithName("detalles")
            .WithMessage("Se requiere al menos un ítem.");

        RuleForEach(x => x.Detalles).ChildRules(RequestValidationRules.ApplyItemRules);

        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.Detalles is null) return;
            RequestValidationRules.ValidateTaxCodePairs(
                req.Detalles.Select(i => (i.CodigoImpuesto, i.CodigoPorcentaje)).ToList(),
                (name, msg) => ctx.AddFailure(name, msg));
        });

        RuleFor(x => x)
            .Must(req => req.Detalles is null
                || RequestValidationRules.SatisfiesOver200Rule(
                    req.Detalles.Select(i => (i.Cantidad, i.PrecioUnitario, i.Descuento)),
                    req.Comprador?.Identificacion))
            .WithName("comprador.identificacion")
            .WithMessage("La identificación del comprador es requerida para comprobantes que superen los $200.");
    }
}
