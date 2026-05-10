using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// Configuration POCO for SRI SOAP web service endpoints and timeouts.
/// Bound from appsettings.json section "Sri".
/// </summary>
public class SriConfiguration
{
    public const string SectionName = "Sri";

    /// <summary>
    /// SRI environment: Test (1) or Production (2). Default: Test.
    /// </summary>
    public EnvironmentType Environment { get; set; } = EnvironmentType.Test;

    /// <summary>
    /// URL for the Recepcion (validarComprobante) service.
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// </summary>
    public string RecepcionUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL for the Autorizacion (autorizacionComprobante) service.
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// </summary>
    public string AutorizacionUrl { get; set; } = string.Empty;

    /// <summary>
    /// Production URL for the Recepcion (validarComprobante) service.
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline
    /// </summary>
    public string RecepcionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// Production URL for the Autorizacion (autorizacionComprobante) service.
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline
    /// </summary>
    public string AutorizacionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// HTTP request timeout in seconds. Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for transient SRI errors. Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}
