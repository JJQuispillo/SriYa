using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Domain.ValueObjects;

/// <summary>
/// Represents a 49-digit SRI access key (clave de acceso) with Mod11 check digit validation.
/// </summary>
public sealed record AccessKey
{
    public string Value { get; }

    public AccessKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidAccessKeyException("La clave de acceso no puede estar vacía.");

        if (value.Length != 49)
            throw new InvalidAccessKeyException($"La clave de acceso debe tener exactamente 49 dígitos, se recibieron {value.Length}.");

        if (!value.All(char.IsDigit))
            throw new InvalidAccessKeyException("La clave de acceso debe contener solo dígitos.");

        if (!IsValidMod11(value))
            throw new InvalidAccessKeyException("La clave de acceso tiene un dígito verificador Mod11 inválido.");

        Value = value;
    }

    /// <summary>
    /// Validates the Mod11 check digit (last digit) of the access key.
    /// The check digit is calculated over the first 48 digits using weights 2-7 cycling right-to-left.
    /// </summary>
    private static bool IsValidMod11(string accessKey)
    {
        var digits = accessKey[..48];
        var expectedCheckDigit = accessKey[48] - '0';

        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (digits.Length - 1 - i) % weights.Length;
            sum += (digits[i] - '0') * weights[weightIndex];
        }

        var remainder = sum % 11;
        var checkDigit = 11 - remainder;

        checkDigit = checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };

        return checkDigit == expectedCheckDigit;
    }

    public override string ToString() => Value;
}
