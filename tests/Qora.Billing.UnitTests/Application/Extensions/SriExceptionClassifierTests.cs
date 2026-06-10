using System;
using FluentAssertions;
using Qora.Billing.Application.Extensions;
using Qora.Billing.Domain.Exceptions;
using Xunit;

namespace Qora.Billing.UnitTests.Application.Extensions;

public class SriExceptionClassifierTests
{
    [Fact]
    public void IsSriTransientOrCircuitOpen_HttpRequestException_ReturnsTrue()
    {
        SriExceptionClassifier.IsSriTransientOrCircuitOpen(new HttpRequestException()).Should().BeTrue();
    }

    [Fact]
    public void IsSriTransientOrCircuitOpen_TaskCanceledException_ReturnsTrue()
    {
        SriExceptionClassifier.IsSriTransientOrCircuitOpen(new TaskCanceledException()).Should().BeTrue();
    }

    [Fact]
    public void IsSriTransientOrCircuitOpen_SriCircuitOpenException_ReturnsTrue()
    {
        var ex = new SriCircuitOpenException(TimeSpan.FromSeconds(30), new HttpRequestException());
        SriExceptionClassifier.IsSriTransientOrCircuitOpen(ex).Should().BeTrue();
    }

    [Fact]
    public void IsSriTransientOrCircuitOpen_UntranslatedBrokenCircuitExceptionByTypeName_ReturnsFalse()
    {
        // El fallback por nombre de tipo fue removido (sriya-2): Infrastructure (SriSoapClient) traduce
        // SIEMPRE BrokenCircuitException/IsolatedCircuitException de Polly a SriCircuitOpenException en su
        // único punto de salida SOAP, por lo que una BrokenCircuitException sin traducir nunca llega aquí.
        // Una excepción cuyo Type.Name sea "BrokenCircuitException" pero que NO sea SriCircuitOpenException
        // ya no se clasifica como transitoria.
        var ex = new BrokenCircuitException();
        SriExceptionClassifier.IsSriTransientOrCircuitOpen(ex).Should().BeFalse();
    }

    [Fact]
    public void IsSriTransientOrCircuitOpen_UnrelatedException_ReturnsFalse()
    {
        SriExceptionClassifier.IsSriTransientOrCircuitOpen(new InvalidOperationException()).Should().BeFalse();
    }

    /// <summary>
    /// Doble local cuyo <c>Type.Name</c> coincide con el tipo de Polly, usado para verificar que el
    /// fallback por nombre de tipo (removido) ya no clasifica como transitoria una excepción sin traducir.
    /// </summary>
    private sealed class BrokenCircuitException : Exception
    {
    }
}
