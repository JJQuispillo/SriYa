using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// POCO de configuración para los endpoints y timeouts de los servicios web SOAP del SRI.
/// Se enlaza desde la sección "Sri" de appsettings.json.
/// </summary>
public class SriConfiguration
{
    public const string SectionName = "Sri";

    /// <summary>
    /// Ambiente del SRI: Test (1) o Production (2). Por defecto: Test.
    /// </summary>
    public EnvironmentType Environment { get; set; } = EnvironmentType.Test;

    /// <summary>
    /// URL del servicio de Recepcion (validarComprobante).
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// </summary>
    public string RecepcionUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL del servicio de Autorizacion (autorizacionComprobante).
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// </summary>
    public string AutorizacionUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL de producción del servicio de Recepcion (validarComprobante).
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline
    /// </summary>
    public string RecepcionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// URL de producción del servicio de Autorizacion (autorizacionComprobante).
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline
    /// </summary>
    public string AutorizacionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// Timeout de las solicitudes HTTP en segundos. Por defecto: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Número máximo de intentos de reintento para errores transitorios del SRI. Por defecto: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}
