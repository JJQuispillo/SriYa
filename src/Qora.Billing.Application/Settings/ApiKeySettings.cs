namespace Qora.Billing.Application.Settings;

/// <summary>
/// Configuration settings for API key generation.
/// </summary>
public class ApiKeySettings
{
    public const string SectionName = "ApiKey";

    /// <summary>
    /// The environment name used to determine API key prefix.
    /// "Production" uses "qora_live_", all other values use "qora_test_".
    /// </summary>
    public string Environment { get; set; } = "Production";

    /// <summary>
    /// Returns the appropriate API key prefix based on the configured environment.
    /// </summary>
    public string Prefix => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase)
        ? "qora_live_"
        : "qora_test_";
}
