using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Persistence.Repositories;

public class DocumentRepository : IDocumentRepository
{
    private readonly BillingDbContext _context;
    private readonly BillingPrivilegedDbContext _privilegedContext;

    public DocumentRepository(BillingDbContext context, BillingPrivilegedDbContext privilegedContext)
    {
        _context = context;
        _privilegedContext = privilegedContext;
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<Document?> GetByAccessKeyAsync(string accessKey, CancellationToken cancellationToken = default)
    {
        // Camino cross-tenant deliberado: la búsqueda por clave de acceso no lleva contexto de tenant.
        // Conexión privilegiada (BYPASSRLS) + IgnoreQueryFilters para sobrevivir al flip fail-closed de P1.
        // AccessKey es un value object mapeado a una sola columna por conversor: se compara contra el VO
        // completo (no contra .Value, que no es traducible a SQL en Postgres real).
        var accessKeyValue = new AccessKey(accessKey);
        return await _privilegedContext.Documents
            .IgnoreQueryFilters()
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.AccessKey == accessKeyValue, cancellationToken);
    }

    public async Task<Document?> GetByBusinessIdentityAsync(
        Domain.Enums.DocumentType documentType, string estab, string ptoEmision, string secuencial,
        CancellationToken cancellationToken = default)
    {
        // Camino acotado por tenant: corre bajo el contexto normal (RLS + filtros fail-closed).
        // Las columnas estab/pto_emision/secuencial son char(N) en Postgres → padded a la derecha con
        // espacios; EF traduce la igualdad correctamente (Npgsql trata el comparando como el mismo tipo).
        return await _context.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(
                d => d.DocumentType == documentType
                     && d.Estab == estab
                     && d.PtoEmision == ptoEmision
                     && d.Secuencial == secuencial,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<Document> Items, int TotalCount)> GetByTenantIdAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _context.Documents
            .Where(d => d.TenantId == tenantId)
            .OrderByDescending(d => d.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(d => d.Items)
            .ToListAsync(cancellationToken);

        return (items.AsReadOnly(), totalCount);
    }

    public async Task<IReadOnlyList<Document>> GetPendingRetryAsync(
        DateTime before, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        // Camino cross-tenant deliberado: el escaneo de reintentos abarca todos los tenants.
        // Conexión privilegiada (BYPASSRLS) + IgnoreQueryFilters para sobrevivir al flip fail-closed de P1.
        return await _privilegedContext.Documents
            .IgnoreQueryFilters()
            .Where(d => d.Status == Domain.Enums.DocumentStatus.PendingRetry
                        && d.NextRetryAt.HasValue
                        && d.NextRetryAt.Value <= before)
            .OrderBy(d => d.NextRetryAt)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQL crudo: EF/LINQ no expresa <c>FOR UPDATE</c> de forma estable. El comando corre sobre la
    /// MISMA conexión/transacción que <c>_context</c> (la transacción ambiental del
    /// <c>TenantContextMiddleware</c>, ver design D1), por lo que: (1) el lock se mantiene hasta el
    /// commit/rollback del request; (2) la GUC <c>app.current_tenant</c> está en alcance → RLS filtra
    /// por tenant aun vía SQL crudo (no se necesita BYPASSRLS). Parámetros enlazados (no interpolados).
    /// <c>document_type</c> se almacena como string (HasConversion&lt;string&gt;()) → se pasa <c>.ToString()</c>.
    /// Ver design D3.a (granularidad del lock).
    /// </remarks>
    public async Task<string?> GetMaxSecuencialWithLockAsync(
        Guid tenantId,
        Domain.Enums.DocumentType documentType,
        string estab,
        string ptoEmision,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT secuencial FROM documents
            WHERE tenant_id = @p0
              AND document_type = @p1
              AND estab = @p2
              AND pto_emision = @p3
            ORDER BY secuencial DESC
            LIMIT 1
            FOR UPDATE;";

        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("p0", tenantId));
        command.Parameters.Add(new NpgsqlParameter("p1", documentType.ToString()));
        command.Parameters.Add(new NpgsqlParameter("p2", estab));
        command.Parameters.Add(new NpgsqlParameter("p3", ptoEmision));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s ? s.Trim() : result?.ToString()?.Trim();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Camino cross-tenant deliberado (el reconciliador barre todos los tenants): contexto
    /// privilegiado (BYPASSRLS) + <c>IgnoreQueryFilters</c> para sobrevivir al flip fail-closed de P1.
    /// <c>FromSqlRaw</c> con <c>FOR UPDATE SKIP LOCKED</c>: el reconciliador abre su propia transacción
    /// (ver <c>SriReconciliationService</c>) por lo que el lock se sostiene en esa transacción y otros
    /// pods saltan las filas ya tomadas. <c>status</c> se almacena como string (HasConversion) → el
    /// literal del filtro es <c>'SentToSri'</c>. Parámetros enlazados (no interpolados). Ver design D4.
    /// </remarks>
    public async Task<IReadOnlyList<Document>> GetStaleSentToSriAsync(
        DateTime olderThan, int maxResults, CancellationToken cancellationToken = default)
    {
        var olderThanParam = new NpgsqlParameter("p0", olderThan);
        var maxResultsParam = new NpgsqlParameter("p1", maxResults);

        var docs = await _privilegedContext.Documents
            .FromSqlRaw(
                @"SELECT * FROM documents
                  WHERE status = 'SentToSri' AND created_at < @p0
                  ORDER BY created_at ASC
                  LIMIT @p1
                  FOR UPDATE SKIP LOCKED",
                olderThanParam, maxResultsParam)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        return docs.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Camino cross-tenant deliberado (el barrido de RIDE recorre todos los tenants): contexto
    /// privilegiado (BYPASSRLS) + <c>IgnoreQueryFilters</c> para sobrevivir al flip fail-closed de P1.
    /// <c>FromSqlRaw</c> con <c>FOR UPDATE SKIP LOCKED</c>: el RidePdfRetryService abre su propia
    /// transacción por lo que el lock se sostiene en ella y otros pods saltan las filas ya tomadas.
    /// <c>status</c> se almacena como string (HasConversion) → el literal del filtro es <c>'Authorized'</c>.
    /// Parámetros enlazados (no interpolados). Mismo patrón que <see cref="GetStaleSentToSriAsync"/>.
    /// </remarks>
    public async Task<IReadOnlyList<Document>> GetAuthorizedMissingRidePdfAsync(
        DateTime olderThan, int maxRetryCount, int maxResults, CancellationToken cancellationToken = default)
    {
        var olderThanParam = new NpgsqlParameter("p0", olderThan);
        var maxRetryParam = new NpgsqlParameter("p1", maxRetryCount);
        var maxResultsParam = new NpgsqlParameter("p2", maxResults);

        var docs = await _privilegedContext.Documents
            .FromSqlRaw(
                @"SELECT * FROM documents
                  WHERE status = 'Authorized'
                    AND ride_generated_at IS NULL
                    AND ride_retry_count < @p1
                    AND processed_at < @p0
                  ORDER BY processed_at ASC
                  LIMIT @p2
                  FOR UPDATE SKIP LOCKED",
                olderThanParam, maxRetryParam, maxResultsParam)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        return docs.AsReadOnly();
    }

    public async Task<Document> CreateAsync(Document document, CancellationToken cancellationToken = default)
    {
        await _context.Documents.AddAsync(document, cancellationToken);
        return document;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stage-only por contrato (ver <see cref="IDocumentRepository.UpdateAsync"/>). Esta
    /// implementación NO emite <c>UPDATE</c> SQL — sólo marca la entidad como
    /// <c>Modified</c> en el ChangeTracker. El flush es responsabilidad de
    /// <c>IUnitOfWork.SaveChangesAsync</c>.
    /// </remarks>
    public Task UpdateAsync(Document document, CancellationToken cancellationToken = default)
    {
        _context.Documents.Update(document);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await _context.Documents
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document is not null)
        {
            _context.Documents.Remove(document);
        }
    }
}
