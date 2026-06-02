using FluentValidation;
using FluentValidation.Results;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.Validators;

public class ProcessDocumentCommandValidator : AbstractValidator<ProcessDocumentCommand>
{
    /// <summary>
    /// Conjunto estático de pares válidos (TaxTypeCode, PercentageCode) que refleja la tabla sri_tax_codes precargada.
    /// Se usa para validación rápida sin una llamada a la base de datos. La tasa de impuesto la deriva el command handler.
    /// </summary>
    private static readonly HashSet<(string TaxTypeCode, string PercentageCode)> ValidTaxCodes =
    [
        // IVA
        ("2", "0"), ("2", "2"), ("2", "3"), ("2", "4"), ("2", "5"),
        ("2", "6"), ("2", "7"), ("2", "8"), ("2", "10"),
        // ICE
        ("3", "3011"), ("3", "3023"), ("3", "3041"), ("3", "3072"),
        // IRBPNR
        ("5", "5001"),
        // ISD
        ("6", "6001"),
        // Retención Renta
        ("1", "303"), ("1", "304"), ("1", "312"), ("1", "322"), ("1", "332"), ("1", "343"),
    ];

    private static readonly HashSet<string> ValidSustentoDocumentTypes = ["01", "03", "04", "05", "07", "41", "43"];

    public ProcessDocumentCommandValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("El TenantId es requerido.");

        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("El cuerpo de la solicitud es requerido.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.TipoDocumento)
                .IsInEnum()
                .WithMessage("El tipo de documento debe ser un valor válido.");

            RuleFor(x => x.Request.Emisor)
                .NotNull()
                .WithMessage("La información del emisor es requerida.")
                .Must(info => info != null && info.ContainsKey("ruc") && !string.IsNullOrWhiteSpace(info["ruc"]))
                .WithMessage("La información del emisor debe contener un RUC válido.")
                .Must(info => info != null && info.ContainsKey("razonSocial") && !string.IsNullOrWhiteSpace(info["razonSocial"]))
                .WithMessage("La información del emisor debe contener la razón social.");

            RuleFor(x => x.Request.Emisor)
                .Must(info =>
                {
                    if (info is null || !info.TryGetValue("ruc", out var ruc)) return false;
                    return ruc.Length == 13 && ruc.All(char.IsDigit);
                })
                .WithMessage("El RUC del emisor debe tener exactamente 13 dígitos.");

            RuleFor(x => x.Request.Comprador)
                .NotNull()
                .WithMessage("La información del comprador es requerida.");

            RuleFor(x => x.Request.Detalles)
                .NotNull()
                .WithMessage("La lista de ítems es requerida.")
                .Must(items => items != null && items.Count > 0)
                .WithMessage("Se requiere al menos un ítem.");

            // Validación a nivel de ítem: reglas comunes (descripción, cantidad, precio, códigos)
            RuleForEach(x => x.Request.Detalles).ChildRules(item =>
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

            // Valida que CodigoImpuesto + CodigoPorcentaje existan en la tabla de referencia del SRI
            RuleFor(x => x.Request).Custom((req, ctx) =>
            {
                if (req.Detalles is null) return;
                for (var index = 0; index < req.Detalles.Count; index++)
                {
                    var item = req.Detalles[index];
                    if (string.IsNullOrWhiteSpace(item.CodigoImpuesto) || string.IsNullOrWhiteSpace(item.CodigoPorcentaje))
                        continue; // ya capturado por las reglas de campo individuales de arriba

                    if (!ValidTaxCodes.Contains((item.CodigoImpuesto, item.CodigoPorcentaje)))
                    {
                        ctx.AddFailure(new ValidationFailure(
                            $"detalles[{index}].codigoPorcentaje",
                            $"El código de impuesto '{item.CodigoImpuesto}/{item.CodigoPorcentaje}' no es válido según la tabla de códigos SRI."));
                    }
                }
            });

            When(x => x.Request.TipoDocumento == DocumentType.ComprobanteRetencion, () =>
            {

                // T5-002: los ítems de retención deben tener todos los campos de sustento completados
                RuleFor(x => x.Request).Custom((req, ctx) =>
                {
                    if (req.Detalles is null) return;
                    for (var index = 0; index < req.Detalles.Count; index++)
                    {
                        var item = req.Detalles[index];

                        if (string.IsNullOrWhiteSpace(item.TipoDocSustento))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"detalles[{index}].tipoDocSustento",
                                "El tipo de documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }
                        else if (!ValidSustentoDocumentTypes.Contains(item.TipoDocSustento))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"detalles[{index}].tipoDocSustento",
                                $"El tipo de documento de sustento '{item.TipoDocSustento}' es inválido. Valores válidos de codDocSustento SRI: 01, 03, 04, 05, 07, 41, 43."));
                        }

                        if (string.IsNullOrWhiteSpace(item.NumDocSustento))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"detalles[{index}].numDocSustento",
                                "El número del documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }

                        if (string.IsNullOrWhiteSpace(item.NumAutDocSustento))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"detalles[{index}].numAutDocSustento",
                                "El número de autorización del documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }

                        if (item.FechaEmisionDocSustento is null)
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"detalles[{index}].fechaEmisionDocSustento",
                                "La fecha de emisión del documento de sustento es requerida para ítems de ComprobanteRetencion."));
                        }
                    }
                });
            });

            // La identificación del comprador es requerida cuando el subtotal supera los $200
            // Nota: el command handler deriva la tasa de impuesto de la tabla del SRI, por lo que aquí usamos solo el subtotal.
            RuleFor(x => x.Request)
                .Must(req =>
                {
                    if (req.Detalles is null || req.Detalles.Count == 0) return true;
                    var subtotal = req.Detalles.Sum(i => (i.Cantidad * i.PrecioUnitario) - i.Descuento);
                    if (subtotal <= 200m) return true;
                    return req.Comprador != null
                           && req.Comprador.ContainsKey("identificacion")
                           && !string.IsNullOrWhiteSpace(req.Comprador["identificacion"]);
                })
                .WithMessage("La identificación del comprador es requerida para comprobantes que superen los $200.");
        });
    }
}
