using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private const string PostgresUniqueViolation = "23505";
    private const string BusinessIdentityConstraint = "ux_documents_business_identity";

    private readonly BillingDbContext _context;
    private IDbContextTransaction? _currentTransaction;

    public UnitOfWork(BillingDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsBusinessIdentityViolation(ex))
        {
            // II-2: una segunda emisión con la misma identidad de negocio (tenant, tipo, estab, ptoEmi,
            // secuencial) viola ux_documents_business_identity. Se traduce a una excepción de dominio para
            // que el handler la deduplique devolviendo el comprobante existente en lugar de un 500.
            throw new DuplicateBusinessIdentityException(
                "Ya existe un comprobante con la misma identidad de negocio (tenant, tipo, estab, ptoEmi, secuencial).",
                ex);
        }
    }

    private static bool IsBusinessIdentityViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresUniqueViolation,
            ConstraintName: BusinessIdentityConstraint,
        };

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is not null)
        {
            throw new InvalidOperationException("Ya hay una transacción en curso.");
        }

        _currentTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
        {
            throw new InvalidOperationException("No hay ninguna transacción en curso para confirmar.");
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await _currentTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await RollbackTransactionAsync(cancellationToken);
            throw;
        }
        finally
        {
            await DisposeTransactionAsync();
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
        {
            throw new InvalidOperationException("No hay ninguna transacción en curso para revertir.");
        }

        try
        {
            await _currentTransaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await DisposeTransactionAsync();
        }
    }

    private async Task DisposeTransactionAsync()
    {
        if (_currentTransaction is not null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public void Dispose()
    {
        _currentTransaction?.Dispose();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
