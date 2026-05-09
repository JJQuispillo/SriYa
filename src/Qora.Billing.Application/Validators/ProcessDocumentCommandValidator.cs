using FluentValidation;
using FluentValidation.Results;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.Validators;

public class ProcessDocumentCommandValidator : AbstractValidator<ProcessDocumentCommand>
{
    /// <summary>
    /// Static set of valid (TaxTypeCode, PercentageCode) pairs mirroring the seeded sri_tax_codes table.
    /// Used for fast validation without a DB call. TaxRate is derived by the command handler.
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
            RuleFor(x => x.Request.DocumentType)
                .IsInEnum()
                .WithMessage("El tipo de documento debe ser un valor válido.");

            RuleFor(x => x.Request.IssuerInfo)
                .NotNull()
                .WithMessage("La información del emisor es requerida.")
                .Must(info => info != null && info.ContainsKey("ruc") && !string.IsNullOrWhiteSpace(info["ruc"]))
                .WithMessage("La información del emisor debe contener un RUC válido.")
                .Must(info => info != null && info.ContainsKey("razonSocial") && !string.IsNullOrWhiteSpace(info["razonSocial"]))
                .WithMessage("La información del emisor debe contener la razón social.");

            RuleFor(x => x.Request.IssuerInfo)
                .Must(info =>
                {
                    if (info is null || !info.TryGetValue("ruc", out var ruc)) return false;
                    return ruc.Length == 13 && ruc.All(char.IsDigit);
                })
                .WithMessage("El RUC del emisor debe tener exactamente 13 dígitos.");

            RuleFor(x => x.Request.BuyerInfo)
                .NotNull()
                .WithMessage("La información del comprador es requerida.");

            RuleFor(x => x.Request.Items)
                .NotNull()
                .WithMessage("La lista de ítems es requerida.")
                .Must(items => items != null && items.Count > 0)
                .WithMessage("Se requiere al menos un ítem.");

            // Item-level validation: common rules (description, quantity, price, codes)
            RuleForEach(x => x.Request.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.Description)
                    .NotEmpty()
                    .WithMessage("La descripción del ítem es requerida.");

                item.RuleFor(i => i.Quantity)
                    .GreaterThan(0)
                    .WithMessage("La cantidad del ítem debe ser mayor a 0.");

                item.RuleFor(i => i.UnitPrice)
                    .GreaterThanOrEqualTo(0)
                    .WithMessage("El precio unitario del ítem no puede ser negativo.");

                item.RuleFor(i => i.MainCode)
                    .NotEmpty()
                    .WithMessage("El código principal del ítem es requerido.");

                item.RuleFor(i => i.TaxCode)
                    .NotEmpty()
                    .WithMessage("El código de impuesto del ítem es requerido.");

                item.RuleFor(i => i.TaxPercentageCode)
                    .NotEmpty()
                    .WithMessage("El código de porcentaje de impuesto del ítem es requerido.");
            });

            // Validate that TaxCode + TaxPercentageCode exist in the SRI reference table
            RuleFor(x => x.Request).Custom((req, ctx) =>
            {
                if (req.Items is null) return;
                for (var index = 0; index < req.Items.Count; index++)
                {
                    var item = req.Items[index];
                    if (string.IsNullOrWhiteSpace(item.TaxCode) || string.IsNullOrWhiteSpace(item.TaxPercentageCode))
                        continue; // already caught by individual field rules above

                    if (!ValidTaxCodes.Contains((item.TaxCode, item.TaxPercentageCode)))
                    {
                        ctx.AddFailure(new ValidationFailure(
                            $"Request.Items[{index}].TaxPercentageCode",
                            $"El código de impuesto '{item.TaxCode}/{item.TaxPercentageCode}' no es válido según la tabla de códigos SRI."));
                    }
                }
            });

            When(x => x.Request.DocumentType == DocumentType.ComprobanteRetencion, () =>
            {

                // T5-002: Retención items must have all sustento fields populated
                RuleFor(x => x.Request).Custom((req, ctx) =>
                {
                    if (req.Items is null) return;
                    for (var index = 0; index < req.Items.Count; index++)
                    {
                        var item = req.Items[index];

                        if (string.IsNullOrWhiteSpace(item.SustentoDocumentType))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentType",
                                "El tipo de documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }
                        else if (!ValidSustentoDocumentTypes.Contains(item.SustentoDocumentType))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentType",
                                $"El tipo de documento de sustento '{item.SustentoDocumentType}' es inválido. Valores válidos de codDocSustento SRI: 01, 03, 04, 05, 07, 41, 43."));
                        }

                        if (string.IsNullOrWhiteSpace(item.SustentoDocumentNumber))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentNumber",
                                "El número del documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }

                        if (string.IsNullOrWhiteSpace(item.SustentoDocumentAuthNumber))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentAuthNumber",
                                "El número de autorización del documento de sustento es requerido para ítems de ComprobanteRetencion."));
                        }

                        if (item.SustentoDocumentIssueDate is null)
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentIssueDate",
                                "La fecha de emisión del documento de sustento es requerida para ítems de ComprobanteRetencion."));
                        }
                    }
                });
            });

            // Buyer identification required when subtotal > $200
            // Note: TaxRate is derived from the SRI table by the command handler, so we use subtotal only here.
            RuleFor(x => x.Request)
                .Must(req =>
                {
                    if (req.Items is null || req.Items.Count == 0) return true;
                    var subtotal = req.Items.Sum(i => (i.Quantity * i.UnitPrice) - i.Discount);
                    if (subtotal <= 200m) return true;
                    return req.BuyerInfo != null
                           && req.BuyerInfo.ContainsKey("identificacion")
                           && !string.IsNullOrWhiteSpace(req.BuyerInfo["identificacion"]);
                })
                .WithMessage("La identificación del comprador es requerida para comprobantes que superen los $200.");
        });
    }
}
