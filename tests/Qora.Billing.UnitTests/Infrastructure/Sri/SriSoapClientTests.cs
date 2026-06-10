using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Infrastructure.Sri;

namespace Qora.Billing.UnitTests.Infrastructure.Sri;

public class SriSoapClientTests
{
    [Fact]
    public void BuildRecepcionSoapEnvelope_ShouldContainXmlContent()
    {
        var xmlBase64 = Convert.ToBase64String("test-xml"u8.ToArray());

        var envelope = SriSoapClient.BuildRecepcionSoapEnvelope(xmlBase64);

        envelope.Should().Contain("validarComprobante");
        envelope.Should().Contain(xmlBase64);
        envelope.Should().Contain("soapenv:Envelope");
    }

    [Fact]
    public void BuildAutorizacionSoapEnvelope_ShouldContainAccessKey()
    {
        var accessKey = "1234567890123456789012345678901234567890123456789";

        var envelope = SriSoapClient.BuildAutorizacionSoapEnvelope(accessKey);

        envelope.Should().Contain("autorizacionComprobante");
        envelope.Should().Contain(accessKey);
        envelope.Should().Contain("claveAccesoComprobante");
    }

    [Fact]
    public void ParseRecepcionResponse_WithRecibida_ShouldReturnAccepted()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion">
                        <RespuestaRecepcionComprobante>
                            <estado>RECIBIDA</estado>
                            <comprobantes/>
                        </RespuestaRecepcionComprobante>
                    </ns2:validarComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseRecepcionResponse(responseXml);

        result.IsAccepted.Should().BeTrue();
        result.Status.Should().Be("RECIBIDA");
    }

    [Fact]
    public void ParseRecepcionResponse_WithDevuelta_ShouldReturnNotAccepted()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion">
                        <RespuestaRecepcionComprobante>
                            <estado>DEVUELTA</estado>
                            <comprobantes>
                                <comprobante>
                                    <mensajes>
                                        <mensaje>
                                            <identificador>35</identificador>
                                            <mensaje>DOCUMENTO DUPLICADO</mensaje>
                                            <informacionAdicional>El comprobante ya fue registrado</informacionAdicional>
                                            <tipo>ERROR</tipo>
                                        </mensaje>
                                    </mensajes>
                                </comprobante>
                            </comprobantes>
                        </RespuestaRecepcionComprobante>
                    </ns2:validarComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseRecepcionResponse(responseXml);

        result.IsAccepted.Should().BeFalse();
        result.Status.Should().Be("DEVUELTA");
        result.Messages.Should().NotBeEmpty();
        result.Messages[0].Should().Contain("DOCUMENTO DUPLICADO");
    }

    [Fact]
    public void ParseAutorizacionResponse_WithAutorizado_ShouldReturnAuthorized()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion">
                        <RespuestaAutorizacionComprobante>
                            <claveAccesoConsultada>1234567890123456789012345678901234567890123456789</claveAccesoConsultada>
                            <numeroComprobantes>1</numeroComprobantes>
                            <autorizaciones>
                                <autorizacion>
                                    <estado>AUTORIZADO</estado>
                                    <numeroAutorizacion>1803202601179226807100110010010000000123728168114</numeroAutorizacion>
                                    <fechaAutorizacion>2026-03-18T10:30:00-05:00</fechaAutorizacion>
                                    <ambiente>PRUEBAS</ambiente>
                                    <comprobante><![CDATA[<factura>...</factura>]]></comprobante>
                                    <mensajes/>
                                </autorizacion>
                            </autorizaciones>
                        </RespuestaAutorizacionComprobante>
                    </ns2:autorizacionComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseAutorizacionResponse(responseXml);

        result.IsAuthorized.Should().BeTrue();
        result.Status.Should().Be("AUTORIZADO");
        result.AuthorizationNumber.Should().NotBeNullOrWhiteSpace();
        result.AuthorizationDate.Should().NotBeNull();
    }

    [Fact]
    public void ParseAutorizacionResponse_WithNoAutorizacion_ShouldReturnNotFound()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion">
                        <RespuestaAutorizacionComprobante>
                            <claveAccesoConsultada>1234567890123456789012345678901234567890123456789</claveAccesoConsultada>
                            <numeroComprobantes>0</numeroComprobantes>
                            <autorizaciones/>
                        </RespuestaAutorizacionComprobante>
                    </ns2:autorizacionComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseAutorizacionResponse(responseXml);

        result.IsAuthorized.Should().BeFalse();
        result.Status.Should().Be("NO_ENCONTRADO");
        result.AuthorizationNumber.Should().BeNull();
    }

    [Fact]
    public void ParseAutorizacionResponse_WithNoAutorizado_ShouldReturnNotAuthorized()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion">
                        <RespuestaAutorizacionComprobante>
                            <claveAccesoConsultada>1234567890123456789012345678901234567890123456789</claveAccesoConsultada>
                            <numeroComprobantes>1</numeroComprobantes>
                            <autorizaciones>
                                <autorizacion>
                                    <estado>NO AUTORIZADO</estado>
                                    <mensajes>
                                        <mensaje>
                                            <identificador>39</identificador>
                                            <mensaje>FIRMA INVALIDA</mensaje>
                                            <informacionAdicional>La firma digital no es valida</informacionAdicional>
                                            <tipo>ERROR</tipo>
                                        </mensaje>
                                    </mensajes>
                                </autorizacion>
                            </autorizaciones>
                        </RespuestaAutorizacionComprobante>
                    </ns2:autorizacionComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseAutorizacionResponse(responseXml);

        result.IsAuthorized.Should().BeFalse();
        result.Status.Should().Be("NO AUTORIZADO");
        result.Messages.Should().NotBeEmpty();
        result.Messages[0].Should().Contain("FIRMA INVALIDA");
    }

    [Fact]
    public void ParseRecepcionResponse_WithMissingSoapBody_ShouldThrow()
    {
        var badXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <root><nothing/></root>
            """;

        var act = () => SriSoapClient.ParseRecepcionResponse(badXml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Body*");
    }

    [Fact]
    public void ParseRecepcionResponse_WithMultipleMessages_ShouldReturnAll()
    {
        var responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                <soap:Body>
                    <ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion">
                        <RespuestaRecepcionComprobante>
                            <estado>DEVUELTA</estado>
                            <comprobantes>
                                <comprobante>
                                    <mensajes>
                                        <mensaje>
                                            <identificador>35</identificador>
                                            <mensaje>ERROR 1</mensaje>
                                            <tipo>ERROR</tipo>
                                        </mensaje>
                                        <mensaje>
                                            <identificador>36</identificador>
                                            <mensaje>ERROR 2</mensaje>
                                            <tipo>ERROR</tipo>
                                        </mensaje>
                                    </mensajes>
                                </comprobante>
                            </comprobantes>
                        </RespuestaRecepcionComprobante>
                    </ns2:validarComprobanteResponse>
                </soap:Body>
            </soap:Envelope>
            """;

        var result = SriSoapClient.ParseRecepcionResponse(responseXml);

        result.Messages.Should().HaveCount(2);
    }

    // URL selection tests
    private static (SriSoapClient client, Mock<HttpMessageHandler> handlerMock) CreateClientWithHandler(
        SriConfiguration config, string responseXml, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseXml)
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var options = Options.Create(config);
        var logger = NullLogger<SriSoapClient>.Instance;
        var client = new SriSoapClient(httpClient, options, logger);
        return (client, handlerMock);
    }

    private static string RecepcionRecibidaResponse => """
        <?xml version="1.0" encoding="UTF-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
            <soap:Body>
                <ns2:validarComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.recepcion">
                    <RespuestaRecepcionComprobante>
                        <estado>RECIBIDA</estado>
                        <comprobantes/>
                    </RespuestaRecepcionComprobante>
                </ns2:validarComprobanteResponse>
            </soap:Body>
        </soap:Envelope>
        """;

    private static string AutorizacionResponse => """
        <?xml version="1.0" encoding="UTF-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
            <soap:Body>
                <ns2:autorizacionComprobanteResponse xmlns:ns2="http://ec.gob.sri.ws.autorizacion">
                    <RespuestaAutorizacionComprobante>
                        <claveAccesoConsultada>1234567890123456789012345678901234567890123456789</claveAccesoConsultada>
                        <numeroComprobantes>0</numeroComprobantes>
                        <autorizaciones/>
                    </RespuestaAutorizacionComprobante>
                </ns2:autorizacionComprobanteResponse>
            </soap:Body>
        </soap:Envelope>
        """;

    [Fact]
    public async Task SendDocumentAsync_WhenTestEnvironment_UsesRecepcionUrl()
    {
        var config = new SriConfiguration
        {
            Environment = EnvironmentType.Test,
            RecepcionUrl = "https://celcer.sri.gob.ec/recepcion-test",
            RecepcionUrlProduccion = "https://cel.sri.gob.ec/recepcion-prod",
            AutorizacionUrl = "https://celcer.sri.gob.ec/autorizacion-test",
            AutorizacionUrlProduccion = "https://cel.sri.gob.ec/autorizacion-prod"
        };
        var (client, handlerMock) = CreateClientWithHandler(config, RecepcionRecibidaResponse);

        await client.SendDocumentAsync("<factura/>");

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri != null &&
                r.RequestUri.ToString().Contains("celcer.sri.gob.ec")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendDocumentAsync_WhenProductionEnvironment_UsesRecepcionUrlProduccion()
    {
        var config = new SriConfiguration
        {
            Environment = EnvironmentType.Production,
            RecepcionUrl = "https://celcer.sri.gob.ec/recepcion-test",
            RecepcionUrlProduccion = "https://cel.sri.gob.ec/recepcion-prod",
            AutorizacionUrl = "https://celcer.sri.gob.ec/autorizacion-test",
            AutorizacionUrlProduccion = "https://cel.sri.gob.ec/autorizacion-prod"
        };
        var (client, handlerMock) = CreateClientWithHandler(config, RecepcionRecibidaResponse);

        await client.SendDocumentAsync("<factura/>");

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri != null &&
                r.RequestUri.ToString().Contains("cel.sri.gob.ec") &&
                !r.RequestUri.ToString().Contains("celcer.sri.gob.ec")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CheckAuthorizationAsync_WhenTestEnvironment_UsesAutorizacionUrl()
    {
        var config = new SriConfiguration
        {
            Environment = EnvironmentType.Test,
            RecepcionUrl = "https://celcer.sri.gob.ec/recepcion-test",
            RecepcionUrlProduccion = "https://cel.sri.gob.ec/recepcion-prod",
            AutorizacionUrl = "https://celcer.sri.gob.ec/autorizacion-test",
            AutorizacionUrlProduccion = "https://cel.sri.gob.ec/autorizacion-prod"
        };
        var (client, handlerMock) = CreateClientWithHandler(config, AutorizacionResponse);

        await client.CheckAuthorizationAsync("1234567890123456789012345678901234567890123456789");

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri != null &&
                r.RequestUri.ToString().Contains("celcer.sri.gob.ec")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CheckAuthorizationAsync_WhenProductionEnvironment_UsesAutorizacionUrlProduccion()
    {
        var config = new SriConfiguration
        {
            Environment = EnvironmentType.Production,
            RecepcionUrl = "https://celcer.sri.gob.ec/recepcion-test",
            RecepcionUrlProduccion = "https://cel.sri.gob.ec/recepcion-prod",
            AutorizacionUrl = "https://celcer.sri.gob.ec/autorizacion-test",
            AutorizacionUrlProduccion = "https://cel.sri.gob.ec/autorizacion-prod"
        };
        var (client, handlerMock) = CreateClientWithHandler(config, AutorizacionResponse);

        await client.CheckAuthorizationAsync("1234567890123456789012345678901234567890123456789");

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri != null &&
                r.RequestUri.ToString().Contains("cel.sri.gob.ec") &&
                !r.RequestUri.ToString().Contains("celcer.sri.gob.ec")),
            ItExpr.IsAny<CancellationToken>());
    }
}
