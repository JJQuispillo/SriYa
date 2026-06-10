namespace Qora.Billing.Application.Settings;

/// <summary>
/// Configuración para la generación de API keys.
/// </summary>
public class ApiKeySettings
{
    public const string SectionName = "ApiKey";

    /// <summary>
    /// Nombre del entorno usado para determinar el prefijo de la API key.
    /// "Production" usa "qora_live_", el resto de valores usan "qora_test_".
    /// </summary>
    public string Environment { get; set; } = "Production";

    /// <summary>
    /// Devuelve el prefijo de API key apropiado según el entorno configurado.
    /// </summary>
    public string Prefix => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase)
        ? "qora_live_"
        : "qora_test_";
}
