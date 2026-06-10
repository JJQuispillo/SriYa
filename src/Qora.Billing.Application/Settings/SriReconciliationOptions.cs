using System.ComponentModel.DataAnnotations;

namespace Qora.Billing.Application.Settings;

/// <summary>
/// Opciones del reconciliador de emisión (cambio sri-emision-atomicidad, design D4 / §3.8).
/// Vinculada a la sección "Sri:Reconciliation" en appsettings.
/// </summary>
/// <remarks>
/// Desviación menor del spec REQ-EMI-030: el spec ubicaba esta POCO en
/// <c>Qora.Billing.Infrastructure/Sri</c>; el design §3.8 la ubica en
/// <c>Qora.Billing.Application/Settings</c> por cohesión con <see cref="EmissionOptions"/>.
/// Se sigue el design (artefacto más reciente). Los defaults usan SEGUNDOS por consistencia
/// (NO minutos como sugería la prosa del spec).
/// </remarks>
public class SriReconciliationOptions
{
    public const string SectionName = "Sri:Reconciliation";

    /// <summary>
    /// Segundos entre barridos de reconciliación. Para DESACTIVAR efectivamente el reconciliador
    /// (escape hatch operacional), poner esto en <c>86400</c> (24h) o mayor. El reconciliador es,
    /// por lo demás, incondicional — ver D9.a.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SweepIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Un documento en <c>SentToSri</c> se considera obsoleto (elegible para reconciliación) cuando su
    /// <c>CreatedAt</c> es anterior a <c>now - StaleSentToSriAfterSeconds</c>. Default 600s = 10 minutos.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int StaleSentToSriAfterSeconds { get; set; } = 600;

    /// <summary>
    /// Máximo de documentos obsoletos procesados por barrido. Acota la duración de la ventana de
    /// retención del lock <c>FOR UPDATE SKIP LOCKED</c>. Lotes mayores disparan múltiples barridos
    /// en sucesión rápida.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxBatchSize { get; set; } = 50;
}
