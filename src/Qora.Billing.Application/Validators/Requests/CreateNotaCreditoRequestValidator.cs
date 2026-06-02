using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

public class CreateNotaCreditoRequestValidator : AbstractValidator<CreateNotaCreditoRequest>
{
    private static readonly HashSet<string> _validDocModificadoCodes = ["01"];

    public CreateNotaCreditoRequestValidator()
    {
        RequestValidationRules.ApplyEmisorBaseRules(this, x => x.Emisor);

        RuleFor(x => x.Comprador)
            .NotNull()
            .WithName("comprador")
            .WithMessage("La información del comprador es requerida.");

        RuleFor(x => x.Sustento)
            .NotNull()
            .WithName("sustento")
            .WithMessage("La información del documento de sustento es requerida.");

        RuleFor(x => x.Sustento.CodDocModificado)
            .Must(c => _validDocModificadoCodes.Contains(c))
            .When(x => x.Sustento is not null)
            .WithName("sustento.codDocModificado")
            .WithMessage("El código del documento modificado es inválido. Valor válido: 01 (Factura).");

        RuleFor(x => x.Sustento.NumDocModificado)
            .NotEmpty()
            .When(x => x.Sustento is not null)
            .WithName("sustento.numDocModificado")
            .WithMessage("El número del documento modificado es requerido.");

        RuleFor(x => x.Sustento.RazonModificacion)
            .NotEmpty()
            .When(x => x.Sustento is not null)
            .WithName("sustento.razonModificacion")
            .WithMessage("La razón de modificación es requerida.");

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
