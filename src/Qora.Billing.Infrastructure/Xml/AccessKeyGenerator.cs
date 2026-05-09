using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Xml;

/// <summary>
/// Generates the 49-digit SRI access key (clave de acceso) with Mod11 check digit.
/// Format: [8:date][2:docType][13:RUC][1:environment][3:series-est][3:series-pto][9:sequential][8:numericCode][1:emissionType][1:checkDigit]
/// </summary>
public static class AccessKeyGenerator
{
    /// <summary>
    /// Builds a 49-digit access key from components.
    /// </summary>
    /// <param name="issueDate">Document issue date (ddMMyyyy).</param>
    /// <param name="documentType">SRI document type code (e.g., 01 for Factura).</param>
    /// <param name="ruc">13-digit RUC of the issuer.</param>
    /// <param name="environment">SRI environment (1=Test, 2=Production).</param>
    /// <param name="establishment">3-digit establishment code (e.g., "001").</param>
    /// <param name="emissionPoint">3-digit emission point code (e.g., "001").</param>
    /// <param name="sequential">9-digit sequential number (e.g., "000000001").</param>
    /// <param name="numericCode">8-digit numeric code for uniqueness.</param>
    /// <param name="emissionType">Emission type (1=Normal).</param>
    /// <returns>A validated AccessKey value object.</returns>
    public static AccessKey Generate(
        DateTime issueDate,
        DocumentType documentType,
        string ruc,
        EnvironmentType environment,
        string establishment,
        string emissionPoint,
        string sequential,
        string numericCode,
        EmissionType emissionType)
    {
        ArgumentNullException.ThrowIfNull(ruc);
        ArgumentNullException.ThrowIfNull(establishment);
        ArgumentNullException.ThrowIfNull(emissionPoint);
        ArgumentNullException.ThrowIfNull(sequential);
        ArgumentNullException.ThrowIfNull(numericCode);

        if (ruc.Length != 13)
            throw new ArgumentException("El RUC debe tener exactamente 13 dígitos.", nameof(ruc));
        if (establishment.Length != 3)
            throw new ArgumentException("El establecimiento debe tener exactamente 3 dígitos.", nameof(establishment));
        if (emissionPoint.Length != 3)
            throw new ArgumentException("El punto de emisión debe tener exactamente 3 dígitos.", nameof(emissionPoint));
        if (sequential.Length != 9)
            throw new ArgumentException("El secuencial debe tener exactamente 9 dígitos.", nameof(sequential));
        if (numericCode.Length != 8)
            throw new ArgumentException("El código numérico debe tener exactamente 8 dígitos.", nameof(numericCode));

        var dateStr = issueDate.ToString("ddMMyyyy");
        var docTypeCode = ((int)documentType).ToString("D2");
        var envCode = ((int)environment).ToString();
        var emissionCode = ((int)emissionType).ToString();

        // 48 digits before check digit: date(8) + docType(2) + ruc(13) + env(1) + est(3) + pto(3) + seq(9) + numCode(8) + emission(1)
        var baseKey = $"{dateStr}{docTypeCode}{ruc}{envCode}{establishment}{emissionPoint}{sequential}{numericCode}{emissionCode}";

        if (baseKey.Length != 48)
            throw new InvalidOperationException($"La clave base debe tener 48 dígitos, se obtuvieron {baseKey.Length}.");

        var checkDigit = CalculateMod11CheckDigit(baseKey);
        var fullKey = baseKey + checkDigit;

        return new AccessKey(fullKey);
    }

    /// <summary>
    /// Calculates the Mod11 check digit for the first 48 digits of an access key.
    /// Uses weights 2-7 cycling from right to left.
    /// </summary>
    public static int CalculateMod11CheckDigit(string digits)
    {
        if (digits.Length != 48)
            throw new ArgumentException("Los dígitos deben tener exactamente 48 caracteres.", nameof(digits));

        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (digits.Length - 1 - i) % weights.Length;
            sum += (digits[i] - '0') * weights[weightIndex];
        }

        var remainder = sum % 11;
        var checkDigit = 11 - remainder;

        return checkDigit switch
        {
            11 => 0,
            10 => 1,
            _ => checkDigit
        };
    }

    /// <summary>
    /// Generates a random 8-digit numeric code for access key uniqueness.
    /// </summary>
    public static string GenerateNumericCode()
    {
        return Random.Shared.Next(10000000, 99999999).ToString("D8");
    }
}
