using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.BackgroundServices;
using Qora.Billing.Infrastructure.Pdf;
using Qora.Billing.Infrastructure.Persistence;
using Qora.Billing.Infrastructure.Persistence.Repositories;
using Qora.Billing.Infrastructure.Signing;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure;

/// <summary>
/// Extension methods for registering all Infrastructure layer services.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Database
        services.AddDbContext<BillingDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("BillingDb")
                    ?? "Host=localhost;Database=billing;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name)));

        // Repositories
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IElectronicSignatureRepository, ElectronicSignatureRepository>();
        services.AddScoped<IUsageRecordRepository, UsageRecordRepository>();
        services.AddScoped<IDocumentEventRepository, DocumentEventRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // XML builders (concrete singleton registrations, one per document type)
        services.AddSingleton<FacturaXmlBuilder>();
        services.AddSingleton<NotaCreditoXmlBuilder>();
        services.AddSingleton<NotaDebitoXmlBuilder>();
        services.AddSingleton<LiquidacionCompraXmlBuilder>();
        services.AddSingleton<GuiaRemisionXmlBuilder>();
        services.AddSingleton<ComprobanteRetencionXmlBuilder>();

        // Document signing
        services.AddSingleton<IDocumentSigner, XadesBesSigner>();

        // SRI SOAP client with Polly resilience (retry, circuit breaker, timeout)
        services.Configure<SriConfiguration>(configuration.GetSection(SriConfiguration.SectionName));
        services.AddSriClientWithResilience();

        // Background services
        services.AddHostedService<SriRetryService>();

        // Document type strategies (IEnumerable<IDocumentTypeStrategy> pattern)
        services.AddScoped<IDocumentTypeStrategy, FacturaStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaCreditoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaDebitoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, LiquidacionCompraStrategy>();
        services.AddScoped<IDocumentTypeStrategy, GuiaRemisionStrategy>();
        services.AddScoped<IDocumentTypeStrategy, ComprobanteRetencionStrategy>();

        // PDF / RIDE generation
        services.AddPdfServices();

        return services;
    }
}
