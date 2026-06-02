using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Events;
using Qora.Billing.Infrastructure.Persistence.Configurations;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// DbContext de EF Core para el microservicio de Billing.
/// Provee global query filters multi-tenant, timestamps automáticos y despacho de eventos de dominio.
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
    /// Establece el tenant actual para el alcance del global query filter.
    /// Debe llamarse antes de consultar entidades acotadas por tenant.
    /// </summary>
    public void SetTenantId(Guid tenantId)
    {
        _currentTenantId = tenantId;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Aplicar todas las configuraciones del assembly (excluye las que necesitan la clave de cifrado)
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(BillingDbContext).Assembly,
            t => t != typeof(TenantConfiguration)
                 && t != typeof(ElectronicSignatureConfiguration));

        // Aplicar por separado las configuraciones que dependen de la clave para pasar la clave de cifrado
        modelBuilder.ApplyConfiguration(new TenantConfiguration(_encryptionKey));
        modelBuilder.ApplyConfiguration(new ElectronicSignatureConfiguration(_encryptionKey));

        // Global query filters para multi-tenancy
        modelBuilder.Entity<Document>()
            .HasQueryFilter(d => !_currentTenantId.HasValue || d.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<ApiKey>()
            .HasQueryFilter(a => !_currentTenantId.HasValue || a.TenantId == _currentTenantId.Value);

        modelBuilder.Entity<ElectronicSignature>()
            .HasQueryFilter(e => !_currentTenantId.HasValue || e.TenantId == _currentTenantId.Value);

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
    /// Establece automáticamente los timestamps CreatedAt y UpdatedAt en las entidades derivadas de BaseEntity.
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
    /// Recolecta los eventos de dominio de todas las entidades rastreadas antes de guardar.
    /// Los eventos se limpian de las entidades para evitar despachos duplicados.
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
    /// Despacha los eventos de dominio recolectados mediante el IPublisher de MediatR.
    /// Los eventos se publican después de que SaveChanges finaliza para garantizar la consistencia de los datos.
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
