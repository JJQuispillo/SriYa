namespace Qora.Billing.Application.Settings;

/// <summary>
/// Opciones de la emisión de comprobantes (cambio sri-emision-atomicidad, design D9.a).
/// Vinculada a la sección "Sri:Emission" en appsettings.
/// </summary>
/// <remarks>
/// La emisión usa siempre el flujo atómico de pre-reserva con 5 checkpoints persistentes; el
/// reconciliador (red de seguridad del bug N1) corre de forma incondicional.
/// La decisión Q5 del spec define <see cref="AutoGenerateSecuencial"/> como un wiring básico
/// (MAX+1) cuya política completa se entrega en el Change #3.
/// </remarks>
public class EmissionOptions
{
    public const string SectionName = "Sri:Emission";

    /// <summary>
    /// Cuando es <c>true</c>, el handler usa <c>MAX(secuencial) + 1</c> (o <c>"000000001"</c> para
    /// la primera emisión) e ignora el valor provisto por el cliente. Cuando es <c>false</c> (por
    /// defecto), el cliente provee el <c>secuencial</c> y el handler lo valida. La política completa
    /// de generación del lado del servidor (análisis de huecos, retroalimentación al cliente, manejo
    /// de huecos) se entrega en el Change #3; este flag sólo habilita el lookup básico de
    /// <c>MAX + 1</c>. Ver REQ-EMI-011.
    /// </summary>
    public bool AutoGenerateSecuencial { get; set; } = false;
}
