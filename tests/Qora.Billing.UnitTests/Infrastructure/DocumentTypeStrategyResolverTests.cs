using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Qora.Billing.Application.Extensions;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.UnitTests.Infrastructure;

public class DocumentTypeStrategyResolverTests
{
    private readonly IOptions<SriConfiguration> _sriOptions;
    private readonly Mock<IRideGenerator> _rideGeneratorMock = new();
    private readonly List<IDocumentTypeStrategy> _strategies;

    public DocumentTypeStrategyResolverTests()
    {
        _sriOptions = Options.Create(new SriConfiguration { Environment = EnvironmentType.Test });

        _strategies =
        [
            new FacturaStrategy(new FacturaXmlBuilder(), _rideGeneratorMock.Object, _sriOptions),
            new NotaCreditoStrategy(new NotaCreditoXmlBuilder(), _rideGeneratorMock.Object, _sriOptions),
            new NotaDebitoStrategy(new NotaDebitoXmlBuilder(), _rideGeneratorMock.Object, _sriOptions),
            new LiquidacionCompraStrategy(new LiquidacionCompraXmlBuilder(), _rideGeneratorMock.Object, _sriOptions),
            new GuiaRemisionStrategy(new GuiaRemisionXmlBuilder(), _rideGeneratorMock.Object, _sriOptions),
            new ComprobanteRetencionStrategy(new ComprobanteRetencionXmlBuilder(), _rideGeneratorMock.Object, _sriOptions)
        ];
    }

    [Fact]
    public void ResolveByDocumentType_Factura_ReturnsFacturaStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.Factura);

        Assert.IsType<FacturaStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_NotaCredito_ReturnsNotaCreditoStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.NotaCredito);

        Assert.IsType<NotaCreditoStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_NotaDebito_ReturnsNotaDebitoStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.NotaDebito);

        Assert.IsType<NotaDebitoStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_LiquidacionCompra_ReturnsLiquidacionCompraStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.LiquidacionCompra);

        Assert.IsType<LiquidacionCompraStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_GuiaRemision_ReturnsGuiaRemisionStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.GuiaRemision);

        Assert.IsType<GuiaRemisionStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_ComprobanteRetencion_ReturnsComprobanteRetencionStrategy()
    {
        var result = _strategies.ResolveByDocumentType(DocumentType.ComprobanteRetencion);

        Assert.IsType<ComprobanteRetencionStrategy>(result);
    }

    [Fact]
    public void ResolveByDocumentType_UnknownType_ThrowsDocumentTypeNotSupportedException()
    {
        var unknownType = (DocumentType)99;

        var act = () => _strategies.ResolveByDocumentType(unknownType);

        act.Should().Throw<DocumentTypeNotSupportedException>()
            .Which.DocumentType.Should().Be(unknownType);
    }

    [Fact]
    public void ResolveByDocumentType_EmptyList_ThrowsDocumentTypeNotSupportedException()
    {
        var emptyStrategies = new List<IDocumentTypeStrategy>();

        var act = () => emptyStrategies.ResolveByDocumentType(DocumentType.Factura);

        act.Should().Throw<DocumentTypeNotSupportedException>()
            .Which.DocumentType.Should().Be(DocumentType.Factura);
    }
}
