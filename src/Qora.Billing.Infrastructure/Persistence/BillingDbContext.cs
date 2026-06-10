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

    /// <summary>
    /// Valor usado por los global query filters fail-closed. Cuando NO hay tenant en contexto vale
    /// <see cref="Guid.Empty"/>, que ninguna fila real lleva como TenantId → la consulta no devuelve nada
    /// (fail-closed, TI-1). Se expone como Guid no-anulable para que EF NO levante <c>.Value</c> sobre un
    /// <c>Guid?</c> nulo al construir el parámetro de la consulta (eso lanzaría "Nullable object must have a value").
    /// </summary>
    private Guid CurrentTenantFilter => _currentTenantId ?? Guid.Empty;

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
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

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
    /// Constructor protegido para contextos derivados (p. ej. <c>BillingPrivilegedDbContext</c>) que
    /// pasan sus propias opciones tipadas. Comparte el mismo modelo y la misma clave de cifrado.
    /// </summary>
    protected BillingDbContext(DbContextOptions options, IConfiguration configuration)
        : base(options)
    {
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

        // Global query filters para multi-tenancy — FAIL-CLOSED (TI-1).
        // Cuando NO hay tenant en contexto, las consultas con alcance de tenant NO devuelven nada
        // (la condición HasValue corta antes de tocar .Value, así que tampoco hay null-deref). El sistema
        // NUNCA cae al comportamiento fail-open de "devolver todas las filas". Esta es la primera línea
        // de defensa; la segunda es RLS en Postgres (current_setting NULL → 0 filas). Los 3 caminos
        // cross-tenant deliberados usan IgnoreQueryFilters SOBRE el contexto privilegiado (BYPASSRLS),
        // por lo que NO dependen del antiguo comportamiento fail-open.
        // Compone fail-closed (TI-1) con el soft-delete (PL-2): las lecturas normales sólo ven documentos
        // del tenant en contexto y NO borrados lógicamente. Los caminos de retención/exportación que
        // necesitan ver las filas borradas usan IgnoreQueryFilters sobre el contexto acotado por tenant.
        modelBuilder.Entity<Document>()
            .HasQueryFilter(d => d.TenantId == CurrentTenantFilter && d.DeletedAt == null);

        modelBuilder.Entity<ApiKey>()
            .HasQueryFilter(a => a.TenantId == CurrentTenantFilter);

        modelBuilder.Entity<ElectronicSignature>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantFilter);

        modelBuilder.Entity<DocumentEvent>()
            .HasQueryFilter(de => de.TenantId == CurrentTenantFilter);

        modelBuilder.Entity<IdempotencyKey>()
            .HasQueryFilter(k => k.TenantId == CurrentTenantFilter);
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
