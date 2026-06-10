using Microsoft.EntityFrameworkCore;
using Npgsql;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// Implementación de <see cref="IIdempotencyStore"/> sobre el contexto normal de la app (billing_app).
/// La tabla idempotency_keys está acotada por tenant y cubierta por RLS: todas estas operaciones corren
/// dentro de la transacción ambiental del request con la GUC app.current_tenant ya fijada, por lo que
/// sólo ven/insertan filas del tenant en contexto.
/// </summary>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private const string PostgresUniqueViolation = "23505";

    private readonly BillingDbContext _context;

    public IdempotencyStore(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<IdempotencyKey?> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _context.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == key, cancellationToken);
    }

    public async Task<bool> TryStartAsync(IdempotencyKey entry, CancellationToken cancellationToken = default)
    {
        await _context.IdempotencyKeys.AddAsync(entry, cancellationToken);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolation })
        {
            // Otra petición concurrente con la misma (tenant, key) ganó la carrera. Desadjuntamos la
            // entidad para que el contexto quede limpio y el llamador pueda releer el registro existente.
            _context.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    public async Task CompleteAsync(IdempotencyKey entry, CancellationToken cancellationToken = default)
    {
        _context.IdempotencyKeys.Update(entry);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
