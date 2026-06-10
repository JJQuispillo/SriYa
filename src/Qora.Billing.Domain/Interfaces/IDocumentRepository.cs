using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Document?> GetByAccessKeyAsync(string accessKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca un documento del tenant actual por su identidad de negocio (DocumentType, estab, ptoEmi,
    /// secuencial). Usado para deduplicar cuando una emisión choca con el unique constraint
    /// ux_documents_business_identity: en vez de un 500, se devuelve el comprobante existente.
    /// Corre bajo el contexto normal (RLS): sólo encuentra documentos del tenant en contexto.
    /// </summary>
    Task<Document?> GetByBusinessIdentityAsync(
        Domain.Enums.DocumentType documentType, string estab, string ptoEmision, string secuencial,
        CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Document> Items, int TotalCount)> GetByTenantIdAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Document>> GetPendingRetryAsync(DateTime before, int maxResults = 100,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Devuelve el <c>secuencial</c> más alto para la tupla de identidad de negocio
    /// (tenant, tipo, estab, ptoEmi), sosteniendo un lock a nivel de fila (Postgres
    /// <c>SELECT ... FOR UPDATE</c>) durante toda la transacción ambiental. Devuelve <c>null</c>
    /// si no existe ningún documento para la tupla.
    /// DEBE invocarse dentro de una transacción abierta (la pre-reserva corre dentro de la
    /// transacción ambiental abierta por <c>TenantContextMiddleware</c>, ver design D1). El lock
    /// se libera en el commit/rollback de esa transacción.
    /// <para>
    /// IMPORTANTE: corre SQL crudo (no expresable en LINQ-to-EF). Los parámetros se enlazan,
    /// nunca se interpolan, para prevenir inyección SQL. Ver design D3.a (granularidad del lock).
    /// </para>
    /// </summary>
    Task<string?> GetMaxSecuencialWithLockAsync(
        Guid tenantId,
        Domain.Enums.DocumentType documentType,
        string estab,
        string ptoEmision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve hasta <paramref name="maxResults"/> documentos en estado <c>SentToSri</c> cuya
    /// <c>CreatedAt</c> es anterior a <paramref name="olderThan"/>, ordenados por <c>CreatedAt ASC</c>.
    /// Usa <c>FOR UPDATE SKIP LOCKED</c> para que múltiples pods que corren el reconciliador no
    /// procesen dos veces el mismo documento. DEBE invocarse dentro de una transacción (el lock se
    /// libera en commit/rollback). Cross-tenant: usa el contexto privilegiado
    /// <c>BillingPrivilegedDbContext</c> (BYPASSRLS), mismo patrón que <c>GetPendingRetryAsync</c>.
    /// Ver design D4.
    /// </summary>
    Task<IReadOnlyList<Document>> GetStaleSentToSriAsync(
        DateTime olderThan, int maxResults, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve hasta <paramref name="maxResults"/> documentos en estado <c>Authorized</c> cuyo RIDE PDF
    /// aún no se generó (<c>ride_generated_at IS NULL</c>), autorizados antes de <paramref name="olderThan"/>
    /// (ventana de obsolescencia) y que no han superado <paramref name="maxRetryCount"/> intentos de
    /// regeneración (<c>ride_retry_count &lt; maxRetryCount</c>), ordenados por <c>processed_at ASC</c>.
    /// Usa <c>FOR UPDATE SKIP LOCKED</c> para que múltiples pods no procesen dos veces el mismo documento.
    /// DEBE invocarse dentro de una transacción (el lock se libera en commit/rollback). Cross-tenant: usa
    /// el contexto privilegiado <c>BillingPrivilegedDbContext</c> (BYPASSRLS), mismo patrón que
    /// <see cref="GetStaleSentToSriAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Document>> GetAuthorizedMissingRidePdfAsync(
        DateTime olderThan, int maxRetryCount, int maxResults, CancellationToken cancellationToken = default);

    Task<Document> CreateAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stagea los cambios de la entidad en el ChangeTracker. El llamador DEBE invocar
    /// <c>IUnitOfWork.SaveChangesAsync(ct)</c> a continuación para persistir.
    /// El flujo de 5 checkpoints del handler de emisión (ver
    /// `sri-emision-atomicidad` design §3.3) depende de este contrato stage-only
    /// para intercalar llamadas a <c>SaveChangesAsync</c> dentro de la transacción
    /// ambiental abierta por `TenantContextMiddleware`.
    /// </summary>
    /// <remarks>
    /// NO agregar <c>SaveChangesAsync</c> dentro de <c>DocumentRepository.UpdateAsync</c>.
    /// El diseño intencional es que el repositorio stagee y la unit of work flushee,
    /// de modo que el handler pueda emitir múltiples ciclos stage+save dentro de una
    /// sola transacción. Call sites que NO respeten este contrato perderán datos en
    /// producción (ver bug N1 del sri-emision-atomicidad change, que perdió
    /// silenciosamente todas las transiciones del retry service durante meses).
    /// </remarks>
    // ARCHITECTURAL GUARANTEE (sri-emision-atomicidad T-EMI-024): todos los call sites de UpdateAsync
    // DEBEN invocar IUnitOfWork.SaveChangesAsync inmediatamente después. Call sites vigentes:
    //   1. SriRetryService.PersistAsync (Infrastructure/BackgroundServices/SriRetryService.cs)
    //   2. ProcessDocumentCommandHandler.IssueDocumentAsync (checkpoints C4/C5)
    //   3. SriReconciliationService.ReconcileDocumentAsync
    // Un 4º call site sin el SaveChangesAsync subsiguiente reintroduce el bug N1.
    Task UpdateAsync(Document document, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
