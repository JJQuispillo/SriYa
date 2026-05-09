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
            throw new InvalidRucException("El RUC no puede estar vacío.");

        if (value.Length != 13)
            throw new InvalidRucException($"El RUC debe tener exactamente 13 dígitos, se recibieron {value.Length}.");

        if (!value.All(char.IsDigit))
            throw new InvalidRucException("El RUC debe contener solo dígitos.");

        if (!value.EndsWith("001"))
            throw new InvalidRucException("El RUC debe terminar en '001' como código de establecimiento.");

        var provinceCode = int.Parse(value[..2]);
        if (provinceCode is not ((>= 1 and <= 24) or 30))
            throw new InvalidRucException($"Código de provincia inválido: {provinceCode}. Debe ser 01-24 o 30.");

        var thirdDigit = value[2] - '0';
        if (thirdDigit > 9)
            throw new InvalidRucException($"Tercer dígito inválido: {thirdDigit}.");

        Value = value;
    }

    public override string ToString() => Value;
}
