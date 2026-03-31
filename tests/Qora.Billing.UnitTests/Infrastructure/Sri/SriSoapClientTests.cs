using FluentAssertions;
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
}
