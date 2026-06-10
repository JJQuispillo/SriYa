using FluentValidation;
using Qora.Billing.Application.DTOs.Requests;

namespace Qora.Billing.Application.Validators.Requests;

/// <summary>
/// Reglas de validación compartidas por los validadores tipados de request.
/// Conserva textualmente los mensajes y conjuntos del antiguo ProcessDocumentCommandValidator.
/// </summary>
internal static class RequestValidationRules
{
    /// <summary>
    /// Pares válidos (TaxTypeCode, PercentageCode) que reflejan la tabla sri_tax_codes.
    /// </summary>
    internal static readonly HashSet<(string TaxTypeCode, string PercentageCode)> ValidTaxCodes =
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

    internal static readonly HashSet<string> ValidSustentoDocumentTypes = ["01", "03", "04", "05", "07", "41", "43"];

    /// <summary>Tipos de identificación de proveedor válidos para Liquidación de Compra.</summary>
    internal static readonly HashSet<string> ValidProviderIdTypes = ["04", "05", "06", "07", "08", "09"];

    /// <summary>Reglas comunes del emisor base (ruc 13 dígitos, razonSocial).</summary>
    internal static void ApplyEmisorBaseRules<T>(AbstractValidator<T> validator, Func<T, EmisorBaseDto> selector)
    {
        validator.RuleFor(x => selector(x))
            .NotNull()
            .WithName("emisor")
            .WithMessage("La información del emisor es requerida.");

        validator.RuleFor(x => selector(x).RazonSocial)
            .NotEmpty()
            .WithName("emisor.razonSocial")
            .WithMessage("La información del emisor debe contener la razón social.");

        validator.RuleFor(x => selector(x).Ruc)
            .Must(IsValidRuc)
            .WithName("emisor.ruc")
            .WithMessage("El RUC del emisor debe tener exactamente 13 dígitos.");

        // Secuencial: opcional (ausente en modo AUTO). Cuando está presente, debe ser
        // exactamente 9 dígitos numéricos. La obligatoriedad en modo CLIENT se valida en
        // el handler (que conoce el modo del tenant); aquí solo validamos el formato.
        validator.RuleFor(x => selector(x).Secuencial)
            .Must(IsValidSecuencial)
            .When(x => !string.IsNullOrEmpty(selector(x).Secuencial))
            .WithName("emisor.secuencial")
            .WithMessage("El secuencial del emisor debe tener exactamente 9 dígitos numéricos.");
    }

    internal static bool IsValidRuc(string? ruc) =>
        !string.IsNullOrEmpty(ruc) && ruc.Length == 13 && ruc.All(char.IsDigit);

    internal static bool IsValidSecuencial(string? secuencial) =>
        !string.IsNullOrEmpty(secuencial) && secuencial.Length == 9 && secuencial.All(char.IsDigit);

    /// <summary>Reglas comunes por ítem estándar (descripción, cantidad, precio, códigos).</summary>
    internal static void ApplyItemRules(InlineValidator<ItemDto> item)
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
    }

    /// <summary>
    /// Valida que cada par (codigoImpuesto, codigoPorcentaje) exista en la tabla SRI.
    /// Agrega fallas en detalles[i].codigoPorcentaje vía el callback addFailure.
    /// </summary>
    internal static void ValidateTaxCodePairs(
        IReadOnlyList<(string CodigoImpuesto, string CodigoPorcentaje)> items,
        Action<string, string> addFailure)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var (codigoImpuesto, codigoPorcentaje) = items[index];
            if (string.IsNullOrWhiteSpace(codigoImpuesto) || string.IsNullOrWhiteSpace(codigoPorcentaje))
                continue; // ya capturado por reglas de campo individuales

            if (!ValidTaxCodes.Contains((codigoImpuesto, codigoPorcentaje)))
            {
                addFailure(
                    $"detalles[{index}].codigoPorcentaje",
                    $"El código de impuesto '{codigoImpuesto}/{codigoPorcentaje}' no es válido según la tabla de códigos SRI.");
            }
        }
    }

    /// <summary>
    /// Regla del subtotal &gt; $200: exige identificación del comprador.
    /// </summary>
    internal static bool SatisfiesOver200Rule(
        IEnumerable<(decimal Cantidad, decimal PrecioUnitario, decimal Descuento)> items,
        string? identificacion)
    {
        var subtotal = items.Sum(i => (i.Cantidad * i.PrecioUnitario) - i.Descuento);
        if (subtotal <= 200m) return true;
        return !string.IsNullOrWhiteSpace(identificacion);
    }
}
