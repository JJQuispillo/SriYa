using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.BackgroundServices;
using Qora.Billing.Infrastructure.Email;
using Qora.Billing.Infrastructure.Pdf;
using Qora.Billing.Infrastructure.Persistence;
using Qora.Billing.Infrastructure.Persistence.Repositories;
using Qora.Billing.Infrastructure.Signing;
using Qora.Billing.Infrastructure.Sri;
using Qora.Billing.Infrastructure.Strategies;
using Qora.Billing.Infrastructure.Xml;

namespace Qora.Billing.Infrastructure;

/// <summary>
/// Métodos de extensión para registrar todos los servicios de la capa de Infrastructure.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Base de datos
        services.AddDbContext<BillingDbContext>(options =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("BillingDb")
                    ?? "Host=localhost;Database=billing;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name));

            // TenantConfiguration y ElectronicSignatureConfiguration requieren la clave de
            // cifrado por constructor, por lo que se aplican manualmente en OnModelCreating y
            // se excluyen del scan del assembly. EF igual las detecta durante el scan y emite
            // este warning; lo silenciamos porque es esperado y benigno.
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.SkippedEntityTypeConfigurationWarning));
        });

        // Repositorios
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IElectronicSignatureRepository, ElectronicSignatureRepository>();
        services.AddScoped<IDocumentEventRepository, DocumentEventRepository>();
        services.AddScoped<ISriTaxCodeRepository, SriTaxCodeRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Constructores de XML (registros singleton concretos, uno por tipo de documento)
        services.AddSingleton<FacturaXmlBuilder>();
        services.AddSingleton<NotaCreditoXmlBuilder>();
        services.AddSingleton<NotaDebitoXmlBuilder>();
        services.AddSingleton<LiquidacionCompraXmlBuilder>();
        services.AddSingleton<GuiaRemisionXmlBuilder>();
        services.AddSingleton<ComprobanteRetencionXmlBuilder>();

        // Mapeo de la interfaz IXmlGenerator (usada por FacturaStrategy)
        services.AddSingleton<IXmlGenerator, FacturaXmlBuilder>();

        // Firma de documentos
        services.AddSingleton<IDocumentSigner, XadesBesSigner>();

        // Cliente SOAP del SRI con resiliencia Polly (retry, circuit breaker, timeout)
        services.Configure<SriConfiguration>(configuration.GetSection(SriConfiguration.SectionName));
        services.AddSriClientWithResilience();

        // Servicios en segundo plano
        services.AddHostedService<SriRetryService>();

        // Estrategias por tipo de documento (patrón IEnumerable<IDocumentTypeStrategy>)
        services.AddScoped<IDocumentTypeStrategy, FacturaStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaCreditoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, NotaDebitoStrategy>();
        services.AddScoped<IDocumentTypeStrategy, LiquidacionCompraStrategy>();
        services.AddScoped<IDocumentTypeStrategy, GuiaRemisionStrategy>();
        services.AddScoped<IDocumentTypeStrategy, ComprobanteRetencionStrategy>();

        // Generación de PDF / RIDE
        services.AddPdfServices();

        // Envío de email
        services.Configure<QoraEmailSettings>(configuration.GetSection(QoraEmailSettings.SectionName));
        services.AddScoped<QoraEmailProvider>();
        services.AddScoped<CustomEmailProvider>();
        services.AddScoped<IEmailService, SmtpEmailService>();

        return services;
    }
}
