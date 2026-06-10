namespace Qora.Billing.Application.Settings;

/// <summary>
/// Configuración del ciclo de vida por emisor (PL-1 exportación / PL-2 borrado con retención).
/// Vinculada a la sección "Lifecycle" en appsettings.
/// </summary>
public class LifecycleSettings
{
    public const string SectionName = "Lifecycle";

    /// <summary>
    /// Cuando es <c>false</c> (por defecto), los comprobantes AUTORIZADOS se anonimizan / borran de forma
    /// lógica en lugar de eliminarse físicamente, preservando los campos fiscales obligatorios (retención
    /// segura). Cuando es <c>true</c>, el borrado con alcance ELIMINA físicamente incluso los autorizados.
    /// Default deliberadamente <c>false</c> (secure-by-default).
    /// </summary>
    public bool AllowHardDeleteAuthorized { get; set; } = false;

    /// <summary>
    /// Formato del archivo de exportación. Por ahora sólo "zip" (PL-1, D5).
    /// </summary>
    public string ExportFormat { get; set; } = "zip";

    /// <summary>
    /// Cuando es <c>true</c> (por defecto), el borrado con alcance exige que se haya producido una
    /// exportación previa (export-always, PL-2). El handler de borrado siempre exporta antes de tocar
    /// cualquier fila; esta bandera es un seguro adicional.
    /// </summary>
    public bool ExportBeforeDelete { get; set; } = true;
}
