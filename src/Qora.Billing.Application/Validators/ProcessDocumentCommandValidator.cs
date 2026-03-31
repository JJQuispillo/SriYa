using FluentValidation;
using FluentValidation.Results;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.Validators;

public class ProcessDocumentCommandValidator : AbstractValidator<ProcessDocumentCommand>
{
    private static readonly HashSet<decimal> ValidIvaRates = [0m, 5m, 15m];
    private static readonly HashSet<decimal> ValidRetencionRates = [1m, 1.75m, 2m, 8m, 10m, 30m];
    private static readonly HashSet<string> ValidSustentoDocumentTypes = ["01", "03", "04", "05", "07", "41", "43"];

    public ProcessDocumentCommandValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required.");

        RuleFor(x => x.Request)
            .NotNull()
            .WithMessage("Request body is required.");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.DocumentType)
                .IsInEnum()
                .WithMessage("DocumentType must be a valid document type.");

            RuleFor(x => x.Request.IssuerInfo)
                .NotNull()
                .WithMessage("IssuerInfo is required.")
                .Must(info => info != null && info.ContainsKey("ruc") && !string.IsNullOrWhiteSpace(info["ruc"]))
                .WithMessage("IssuerInfo must contain a valid RUC.")
                .Must(info => info != null && info.ContainsKey("razonSocial") && !string.IsNullOrWhiteSpace(info["razonSocial"]))
                .WithMessage("IssuerInfo must contain razonSocial (business name).");

            RuleFor(x => x.Request.IssuerInfo)
                .Must(info =>
                {
                    if (info is null || !info.TryGetValue("ruc", out var ruc)) return false;
                    return ruc.Length == 13 && ruc.All(char.IsDigit);
                })
                .WithMessage("IssuerInfo RUC must be exactly 13 digits.");

            RuleFor(x => x.Request.BuyerInfo)
                .NotNull()
                .WithMessage("BuyerInfo is required.");

            RuleFor(x => x.Request.Items)
                .NotNull()
                .WithMessage("Items list is required.")
                .Must(items => items != null && items.Count > 0)
                .WithMessage("At least one item is required.");

            // Item-level validation: common rules (description, quantity, price, codes)
            RuleForEach(x => x.Request.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.Description)
                    .NotEmpty()
                    .WithMessage("Item description is required.");

                item.RuleFor(i => i.Quantity)
                    .GreaterThan(0)
                    .WithMessage("Item quantity must be greater than 0.");

                item.RuleFor(i => i.UnitPrice)
                    .GreaterThanOrEqualTo(0)
                    .WithMessage("Item unit price cannot be negative.");

                item.RuleFor(i => i.MainCode)
                    .NotEmpty()
                    .WithMessage("Item main code is required.");

                item.RuleFor(i => i.TaxCode)
                    .NotEmpty()
                    .WithMessage("Item tax code is required.");

                item.RuleFor(i => i.TaxPercentageCode)
                    .NotEmpty()
                    .WithMessage("Item tax percentage code is required.");
            });

            // T5-001: TaxRate validation — branches by document type
            When(x => x.Request.DocumentType != DocumentType.ComprobanteRetencion, () =>
            {
                RuleFor(x => x.Request).Custom((req, ctx) =>
                {
                    if (req.Items is null) return;
                    for (var index = 0; index < req.Items.Count; index++)
                    {
                        var item = req.Items[index];
                        if (!ValidIvaRates.Contains(item.TaxRate))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].TaxRate",
                                $"IVA rate {item.TaxRate}% is invalid. Valid rates for 2026: 0%, 5%, 15%."));
                        }
                    }
                });
            });

            When(x => x.Request.DocumentType == DocumentType.ComprobanteRetencion, () =>
            {
                // T5-001: Retención TaxRate must be a valid SRI retention percentage
                RuleFor(x => x.Request).Custom((req, ctx) =>
                {
                    if (req.Items is null) return;
                    for (var index = 0; index < req.Items.Count; index++)
                    {
                        var item = req.Items[index];
                        if (!ValidRetencionRates.Contains(item.TaxRate))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].TaxRate",
                                $"Retention rate {item.TaxRate}% is invalid. Valid SRI retention percentages: 1%, 1.75%, 2%, 8%, 10%, 30%."));
                        }
                    }
                });

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
                                "SustentoDocumentType is required for ComprobanteRetencion items."));
                        }
                        else if (!ValidSustentoDocumentTypes.Contains(item.SustentoDocumentType))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentType",
                                $"SustentoDocumentType '{item.SustentoDocumentType}' is invalid. Valid SRI codDocSustento values: 01, 03, 04, 05, 07, 41, 43."));
                        }

                        if (string.IsNullOrWhiteSpace(item.SustentoDocumentNumber))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentNumber",
                                "SustentoDocumentNumber is required for ComprobanteRetencion items."));
                        }

                        if (string.IsNullOrWhiteSpace(item.SustentoDocumentAuthNumber))
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentAuthNumber",
                                "SustentoDocumentAuthNumber is required for ComprobanteRetencion items."));
                        }

                        if (item.SustentoDocumentIssueDate is null)
                        {
                            ctx.AddFailure(new ValidationFailure(
                                $"Request.Items[{index}].SustentoDocumentIssueDate",
                                "SustentoDocumentIssueDate is required for ComprobanteRetencion items."));
                        }
                    }
                });
            });

            // Buyer identification required when total > $200
            RuleFor(x => x.Request)
                .Must(req =>
                {
                    if (req.Items is null || req.Items.Count == 0) return true;
                    var total = req.Items.Sum(i =>
                    {
                        var subtotal = (i.Quantity * i.UnitPrice) - i.Discount;
                        return subtotal + (subtotal * i.TaxRate / 100m);
                    });
                    if (total <= 200m) return true;
                    return req.BuyerInfo != null
                           && req.BuyerInfo.ContainsKey("identificacion")
                           && !string.IsNullOrWhiteSpace(req.BuyerInfo["identificacion"]);
                })
                .WithMessage("Buyer identification is required for invoices exceeding $200.");
        });
    }
}
