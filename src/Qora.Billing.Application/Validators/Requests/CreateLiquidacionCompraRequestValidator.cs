using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

public class CreateLiquidacionCompraRequestValidator : AbstractValidator<CreateLiquidacionCompraRequest>
{
    public CreateLiquidacionCompraRequestValidator()
    {
        RequestValidationRules.ApplyEmisorBaseRules(this, x => x.Emisor);

        RuleFor(x => x.Proveedor)
            .NotNull()
            .WithName("proveedor")
            .WithMessage("La información del proveedor es requerida.");

        RuleFor(x => x.Proveedor.TipoIdentificacionProveedor)
            .Must(t => RequestValidationRules.ValidProviderIdTypes.Contains(t))
            .When(x => x.Proveedor is not null)
            .WithName("proveedor.tipoIdentificacionProveedor")
            .WithMessage("El tipo de identificación del proveedor es inválido. Valores válidos: 04, 05, 06, 07, 08, 09.");

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

        // Retención opcional: todo-o-nada. El record exige los tres campos cuando está presente,
        // pero validamos que no vengan vacíos.
        When(x => x.Retencion is not null, () =>
        {
            RuleFor(x => x.Retencion!.CodigoRetencion)
                .NotEmpty()
                .WithName("retencion.codigoRetencion")
                .WithMessage("El código de retención es requerido cuando se incluye retención.");

            RuleFor(x => x.Retencion!.PorcentajeRetencion)
                .NotEmpty()
                .WithName("retencion.porcentajeRetencion")
                .WithMessage("El porcentaje de retención es requerido cuando se incluye retención.");

            RuleFor(x => x.Retencion!.ValorRetencion)
                .NotEmpty()
                .WithName("retencion.valorRetencion")
                .WithMessage("El valor de retención es requerido cuando se incluye retención.");
        });
    }
}
