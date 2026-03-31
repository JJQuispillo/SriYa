using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Domain.ValueObjects;

/// <summary>
/// Represents a 13-digit Ecuadorian RUC (Registro Unico de Contribuyentes).
/// Validates format: 13 digits, first two are province code (01-24 or 30), third digit determines type.
/// </summary>
public sealed record Ruc
{
    public string Value { get; }

    public Ruc(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidRucException("RUC cannot be empty.");

        if (value.Length != 13)
            throw new InvalidRucException($"RUC must be exactly 13 digits, got {value.Length}.");

        if (!value.All(char.IsDigit))
            throw new InvalidRucException("RUC must contain only digits.");

        if (!value.EndsWith("001"))
            throw new InvalidRucException("RUC must end with '001' for the establishment code.");

        var provinceCode = int.Parse(value[..2]);
        if (provinceCode is not ((>= 1 and <= 24) or 30))
            throw new InvalidRucException($"Invalid province code: {provinceCode}. Must be 01-24 or 30.");

        var thirdDigit = value[2] - '0';
        if (thirdDigit > 9)
            throw new InvalidRucException($"Invalid third digit: {thirdDigit}.");

        Value = value;
    }

    public override string ToString() => Value;
}
