using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// SOAP client for SRI web services. Uses HttpClient directly (no WCF dependency).
/// Two operations: validarComprobante (send) and autorizacionComprobante (check authorization).
/// </summary>
public class SriSoapClient : ISriClient
{
    private readonly HttpClient _httpClient;
    private readonly SriConfiguration _config;
    private readonly ILogger<SriSoapClient> _logger;

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";

    public SriSoapClient(
        HttpClient httpClient,
        IOptions<SriConfiguration> config,
        ILogger<SriSoapClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends a signed XML to SRI's validarComprobante endpoint.
    /// The XML is sent as a Base64-encoded byte array inside the SOAP envelope.
    /// </summary>
    public async Task<SriSendResult> SendDocumentAsync(string signedXml, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signedXml);

        var xmlBytes = Encoding.UTF8.GetBytes(signedXml);
        var xmlBase64 = Convert.ToBase64String(xmlBytes);

        var soapEnvelope = BuildRecepcionSoapEnvelope(xmlBase64);

        _logger.LogInformation("Sending document to SRI recepcion endpoint");

        var recepcionUrl = _config.Environment == EnvironmentType.Production
            ? _config.RecepcionUrlProduccion
            : _config.RecepcionUrl;

        var responseXml = await SendSoapRequestAsync(
            recepcionUrl,
            soapEnvelope,
            "validarComprobante",
            cancellationToken);

        return ParseRecepcionResponse(responseXml);
    }

    /// <summary>
    /// Checks the authorization status of a document by its access key.
    /// </summary>
    public async Task<SriAuthorizationResult> CheckAuthorizationAsync(string accessKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessKey);

        var soapEnvelope = BuildAutorizacionSoapEnvelope(accessKey);

        _logger.LogInformation("Checking authorization for access key {AccessKeyPrefix}...",
            accessKey.Length > 10 ? accessKey[..10] : accessKey);

        var autorizacionUrl = _config.Environment == EnvironmentType.Production
            ? _config.AutorizacionUrlProduccion
            : _config.AutorizacionUrl;

        var responseXml = await SendSoapRequestAsync(
            autorizacionUrl,
            soapEnvelope,
            "autorizacionComprobante",
            cancellationToken);

        return ParseAutorizacionResponse(responseXml);
    }

    private async Task<string> SendSoapRequestAsync(string url, string soapEnvelope,
        string soapAction, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
        request.Headers.Add("SOAPAction", "\"\"");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("SRI SOAP request failed with status {StatusCode}: {Response}",
                response.StatusCode, responseContent);
            throw new HttpRequestException(
                $"La solicitud SOAP al SRI falló con código de estado {response.StatusCode}.");
        }

        return responseContent;
    }

    internal static string BuildRecepcionSoapEnvelope(string xmlBase64)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ec="http://ec.gob.sri.ws.recepcion">
                <soapenv:Header/>
                <soapenv:Body>
                    <ec:validarComprobante>
                        <xml>{xmlBase64}</xml>
                    </ec:validarComprobante>
                </soapenv:Body>
            </soapenv:Envelope>
            """;
    }

    internal static string BuildAutorizacionSoapEnvelope(string accessKey)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ec="http://ec.gob.sri.ws.autorizacion">
                <soapenv:Header/>
                <soapenv:Body>
                    <ec:autorizacionComprobante>
                        <claveAccesoComprobante>{accessKey}</claveAccesoComprobante>
                    </ec:autorizacionComprobante>
                </soapenv:Body>
            </soapenv:Envelope>
            """;
    }

    internal static SriSendResult ParseRecepcionResponse(string responseXml)
    {
        var doc = XDocument.Parse(responseXml);

        // Navigate SOAP envelope to find the response body
        var body = doc.Descendants(SoapNs + "Body").FirstOrDefault()
            ?? throw new InvalidOperationException("Respuesta SOAP inválida: falta el elemento Body.");

        // SRI returns estado = RECIBIDA or DEVUELTA
        var estado = body.Descendants("estado").FirstOrDefault()?.Value ?? "UNKNOWN";
        var isAccepted = estado.Equals("RECIBIDA", StringComparison.OrdinalIgnoreCase);

        var messages = body.Descendants("mensaje")
            .Where(m => m.HasElements) // Only container <mensaje> elements, not leaf text elements
            .Select(m =>
            {
                var identifier = m.Element("identificador")?.Value ?? "";
                var message = m.Element("mensaje")?.Value ?? "";
                var additionalInfo = m.Element("informacionAdicional")?.Value ?? "";
                var type = m.Element("tipo")?.Value ?? "";
                return $"[{type}] {identifier}: {message} {additionalInfo}".Trim();
            })
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();

        return new SriSendResult(isAccepted, estado, messages);
    }

    internal static SriAuthorizationResult ParseAutorizacionResponse(string responseXml)
    {
        var doc = XDocument.Parse(responseXml);

        var body = doc.Descendants(SoapNs + "Body").FirstOrDefault()
            ?? throw new InvalidOperationException("Respuesta SOAP inválida: falta el elemento Body.");

        // Look for autorizacion element inside the response
        var autorizacion = body.Descendants("autorizacion").FirstOrDefault();

        if (autorizacion is null)
        {
            // No authorization found — may be pending
            var numAutorizaciones = body.Descendants("numeroComprobantes").FirstOrDefault()?.Value ?? "0";
            return new SriAuthorizationResult(
                false, null, null,
                "NO_ENCONTRADO",
                [$"No se encontraron autorizaciones. Total comprobantes: {numAutorizaciones}"]);
        }

        var estado = autorizacion.Element("estado")?.Value ?? "UNKNOWN";
        var isAuthorized = estado.Equals("AUTORIZADO", StringComparison.OrdinalIgnoreCase);
        var authNumber = autorizacion.Element("numeroAutorizacion")?.Value;

        DateTime? authDate = null;
        var authDateStr = autorizacion.Element("fechaAutorizacion")?.Value;
        if (!string.IsNullOrWhiteSpace(authDateStr) && DateTime.TryParse(authDateStr, out var parsed))
        {
            authDate = parsed;
        }

        var messages = autorizacion.Descendants("mensaje")
            .Where(m => m.HasElements) // Only container <mensaje> elements, not leaf text elements
            .Select(m =>
            {
                var identifier = m.Element("identificador")?.Value ?? "";
                var message = m.Element("mensaje")?.Value ?? "";
                var additionalInfo = m.Element("informacionAdicional")?.Value ?? "";
                var type = m.Element("tipo")?.Value ?? "";
                return $"[{type}] {identifier}: {message} {additionalInfo}".Trim();
            })
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();

        return new SriAuthorizationResult(isAuthorized, authNumber, authDate, estado, messages);
    }
}
