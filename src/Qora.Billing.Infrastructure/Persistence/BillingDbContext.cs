using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Events;
using Qora.Billing.Infrastructure.Persistence.Configurations;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for the Billing microservice.
/// Provides multi-tenant global query filters, automatic timestamps, and domain event dispatching.
/// </summary>
public class BillingDbContext : DbContext
{
    private Guid? _currentTenantId;
    private readonly IPublisher? _publisher;
    private readonly string _encryptionKey;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentItem> DocumentItems => Set<DocumentItem>();
    public DbSet<Destinatario> Destinatarios => Set<Destinatario>();
    public DbSet<DestinatarioItem> DestinatarioItems => Set<DestinatarioItem>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ElectronicSignature> ElectronicSignatures => Set<ElectronicSignature>();
    public DbSet<DocumentEvent> DocumentEvents => Set<DocumentEvent>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<SriTaxCode> SriTaxCodes => Set<SriTaxCode>();

    public BillingDbContext(DbContextOptions<BillingDbContext> options, IConfiguration configuration)
        : base(options)
    {
        _encryptionKey = configuration["Encryption:Key"] ?? "default-key-change-in-production!!";
    }

    public BillingDbContext(DbContextOptions<BillingDbContext> options, IPublisher publisher, IConfiguration configuration)
        : base(options)
    {
        _publisher = publisher;
        _encryptionKey = configuration["Encryption:Key"] ?? "default-key-change-in-production!!";
    }

    /// <summary>
    /// Sets the current tenant for global query filter scoping.
    /// Must be called before querying tenant-scoped entities.
    /// </summary>
    public void SetTenantId(Guid tenantId)
    {
        _currentTenantId = tenantId;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from assembly (excludes TenantConfiguration which needs the encryption key)
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(BillingDbContext).Assembly,
            t => t != typeof(TenantConfiguration));

        // Apply TenantConfiguration separately to pass the encryption key
        modelBuilder.ApplyConfiguration(new TenantConfiguration(_encryptionKey));

        // Global query filters for multi-tenancy
        modelBuilder.Entity<Document>()
            .HasQueryFilter(d => !_currentTenantId.HasValue || d.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<ApiKey>()
            .HasQueryFilter(a => !_currentTenantId.HasValue || a.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<ElectronicSignature>()
            .HasQueryFilter(e => !_currentTenantId.HasValue || e.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<UsageRecord>()
            .HasQueryFilter(u => !_currentTenantId.HasValue || u.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<DocumentEvent>()
            .HasQueryFilter(de => !_currentTenantId.HasValue || de.TenantId == _currentTenantId.Value);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetTimestamps();
        var domainEvents = CollectDomainEvents();

        var result = await base.SaveChangesAsync(cancellationToken);

        await DispatchDomainEvents(domainEvents);

        return result;
    }

    /// <summary>
    /// Automatically sets CreatedAt and UpdatedAt timestamps on BaseEntity-derived entities.
    /// </summary>
    private void SetTimestamps()
    {
        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            var now = DateTime.UtcNow;

            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(BaseEntity.CreatedAt)).CurrentValue = now;
                entry.Property(nameof(BaseEntity.UpdatedAt)).CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(BaseEntity.UpdatedAt)).CurrentValue = now;
                entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
            }
        }
    }

    /// <summary>
    /// Collects domain events from all tracked entities before saving.
    /// Events are cleared from entities to prevent duplicate dispatching.
    /// </summary>
    private List<DomainEvent> CollectDomainEvents()
    {
        var entities = ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var domainEvents = entities
            .SelectMany(e => e.DomainEvents)
            .ToList();

        foreach (var entity in entities)
        {
            entity.ClearDomainEvents();
        }

        return domainEvents;
    }

    /// <summary>
    /// Dispatches collected domain events via MediatR IPublisher.
    /// Events are published after SaveChanges completes to ensure data consistency.
    /// </summary>
    private async Task DispatchDomainEvents(List<DomainEvent> domainEvents)
    {
        if (_publisher is null || domainEvents.Count == 0)
            return;

        foreach (var domainEvent in domainEvents)
        {
            await _publisher.Publish(domainEvent);
        }
    }
}
