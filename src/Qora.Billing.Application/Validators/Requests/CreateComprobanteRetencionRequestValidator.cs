using System.Text.RegularExpressions;
using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

public partial class CreateComprobanteRetencionRequestValidator : AbstractValidator<CreateComprobanteRetencionRequest>
{
    private static readonly HashSet<string> ValidRetencionTaxCodes = ["1", "2", "6"];

    public CreateComprobanteRetencionRequestValidator()
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

        RuleFor(x => x.Emisor.PeriodoFiscal)
            .Must(p => p is not null && PeriodoFiscalRegex().IsMatch(p))
            .When(x => x.Emisor is not null)
            .WithName("emisor.periodoFiscal")
            .WithMessage("El periodo fiscal debe tener el formato MM/YYYY.");

        RuleFor(x => x.SujetoRetenido)
            .NotNull()
            .WithName("sujetoRetenido")
            .WithMessage("La información del sujeto retenido es requerida.");

        RuleFor(x => x.Detalles)
            .NotNull()
            .Must(items => items != null && items.Count > 0)
            .WithName("detalles")
            .WithMessage("Se requiere al menos un ítem.");

        RuleForEach(x => x.Detalles).ChildRules(item =>
        {
            item.RuleFor(i => i.Descripcion)
                .NotEmpty()
                .WithMessage("La descripción del ítem es requerida.");

            item.RuleFor(i => i.Cantidad)
                .GreaterThan(0)
                .WithMessage("La cantidad del ítem debe ser mayor a 0.");

            item.RuleFor(i => i.PrecioUnitario)
                .GreaterThanOrEqualTo(0)
                .WithMessage("El precio unitario del ítem no puede ser negativo.");

            item.RuleFor(i => i.CodigoPrincipal)
                .NotEmpty()
                .WithMessage("El código principal del ítem es requerido.");

            item.RuleFor(i => i.CodigoImpuesto)
                .NotEmpty()
                .WithMessage("El código de impuesto del ítem es requerido.");

            item.RuleFor(i => i.CodigoPorcentaje)
                .NotEmpty()
                .WithMessage("El código de porcentaje de impuesto del ítem es requerido.");
        });

        // Pares de código tributario válidos según la tabla SRI.
        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.Detalles is null) return;
            RequestValidationRules.ValidateTaxCodePairs(
                req.Detalles.Select(i => (i.CodigoImpuesto, i.CodigoPorcentaje)).ToList(),
                (name, msg) => ctx.AddFailure(name, msg));
        });

        // El tipo de impuesto de cada ítem de retención debe ser 1 (Renta), 2 (IVA) o 6 (ISD).
        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.Detalles is null) return;
            for (var index = 0; index < req.Detalles.Count; index++)
            {
                var codigoImpuesto = req.Detalles[index].CodigoImpuesto;
                if (string.IsNullOrWhiteSpace(codigoImpuesto)) continue;
                if (!ValidRetencionTaxCodes.Contains(codigoImpuesto))
                {
                    ctx.AddFailure(
                        $"detalles[{index}].codigoImpuesto",
                        $"El código de impuesto de retención '{codigoImpuesto}' es inválido. Valores válidos: 1 (Renta), 2 (IVA), 6 (ISD).");
                }
            }
        });

        // Sustento por ítem: todos los campos completos y tipoDocSustento válido.
        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.Detalles is null) return;
            for (var index = 0; index < req.Detalles.Count; index++)
            {
                var item = req.Detalles[index];

                if (string.IsNullOrWhiteSpace(item.TipoDocSustento))
                {
                    ctx.AddFailure(
                        $"detalles[{index}].tipoDocSustento",
                        "El tipo de documento de sustento es requerido para ítems de ComprobanteRetencion.");
                }
                else if (!RequestValidationRules.ValidSustentoDocumentTypes.Contains(item.TipoDocSustento))
                {
                    ctx.AddFailure(
                        $"detalles[{index}].tipoDocSustento",
                        $"El tipo de documento de sustento '{item.TipoDocSustento}' es inválido. Valores válidos de codDocSustento SRI: 01, 03, 04, 05, 07, 41, 43.");
                }

                if (string.IsNullOrWhiteSpace(item.NumDocSustento))
                {
                    ctx.AddFailure(
                        $"detalles[{index}].numDocSustento",
                        "El número del documento de sustento es requerido para ítems de ComprobanteRetencion.");
                }

                if (string.IsNullOrWhiteSpace(item.NumAutDocSustento))
                {
                    ctx.AddFailure(
                        $"detalles[{index}].numAutDocSustento",
                        "El número de autorización del documento de sustento es requerido para ítems de ComprobanteRetencion.");
                }

                if (item.FechaEmisionDocSustento == default)
                {
                    ctx.AddFailure(
                        $"detalles[{index}].fechaEmisionDocSustento",
                        "La fecha de emisión del documento de sustento es requerida para ítems de ComprobanteRetencion.");
                }
            }
        });
    }

    [GeneratedRegex(@"^(0[1-9]|1[0-2])/\d{4}$")]
    private static partial Regex PeriodoFiscalRegex();
}
