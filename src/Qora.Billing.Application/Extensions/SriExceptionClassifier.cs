using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Application.Extensions;

/// <summary>
/// Fuente única de verdad para "¿es esta excepción reintentable / transitoria del SRI?"
/// (cambio sri-emision-atomicidad, design D5; REQ-EMI-013, REQ-EMI-015).
/// </summary>
/// <remarks>
/// <para>
/// El nuevo flujo de emisión y el reconciliador necesitan distinguir las fallas transitorias del SRI
/// (timeout HTTP, cancelación, circuito abierto) de las fallas permanentes. Para las transitorias el
/// documento se deja en su último estado persistido (Signed/SentToSri) y el reconciliador lo recoge;
/// para el resto se propaga el error.
/// </para>
/// <para>
/// El circuito abierto se reconoce mediante <see cref="SriCircuitOpenException"/> (Domain), con un
/// chequeo de tipo <c>is</c> (compile-time safe). La capa Infrastructure (<c>SriSoapClient</c>)
/// traduce SIEMPRE las excepciones de Polly (<c>BrokenCircuitException</c> e
/// <c>IsolatedCircuitException</c>) a <see cref="SriCircuitOpenException"/> en su único punto de salida
/// SOAP (<c>SendSoapRequestAsync</c>, usado tanto por <c>SendDocumentAsync</c> como por
/// <c>CheckAuthorizationAsync</c>), por lo que una <c>BrokenCircuitException</c> de Polly nunca cruza a
/// Application sin traducir. Por eso aquí basta el chequeo de tipo y NO se necesita un fallback por
/// nombre de tipo.
/// </para>
/// </remarks>
public static class SriExceptionClassifier
{
    /// <summary>
    /// Devuelve <c>true</c> para fallas transitorias del SRI: <see cref="HttpRequestException"/>,
    /// <see cref="TaskCanceledException"/> y <see cref="SriCircuitOpenException"/> (chequeo de tipo).
    /// </summary>
    public static bool IsSriTransientOrCircuitOpen(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex is HttpRequestException or TaskCanceledException or SriCircuitOpenException;
    }
}
