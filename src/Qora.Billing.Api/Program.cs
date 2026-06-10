using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Api.Endpoints;
using Qora.Billing.Api.Middleware;
using Qora.Billing.Application;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Infrastructure;
using Qora.Billing.Infrastructure.Persistence;
using Serilog;

// ─── Inicialización de Serilog ───────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ─────────────────────────────────────────────────────
    // Reemplaza el bootstrap logger con la configuración final (lee de appsettings).
    // Se construye el Logger final como instancia y se registra con AddSerilog(logger)
    // en lugar del overload de reconfiguración. Esto evita la reentrada de Freeze()
    // del ReloadableLogger durante Build() ("The logger is already frozen") que ocurre
    // con CreateBootstrapLogger() + el overload de callback en Serilog.AspNetCore 9/10.
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .CreateLogger();
    builder.Services.AddSerilog(Log.Logger);

    // ─── Capa de Aplicación ───────────────────────────────────────────
    builder.Services.AddApplicationServices();

    // ─── Configuración de API Key (prefijo según el ambiente) ──────────────
    builder.Services.Configure<Qora.Billing.Application.Settings.ApiKeySettings>(options =>
    {
        options.Environment = builder.Environment.IsProduction() ? "Production" : "Test";
    });

    // ─── Capa de Infraestructura (BD, repos, XML, firma, SRI, PDF) ───
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // ─── Contexto de Tenant (con alcance por solicitud) ─────────────────────────
    builder.Services.AddScoped<ITenantContext, TenantContext>();

    // ─── Autenticación ──────────────────────────────────────────────
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = "MultiAuth";
            options.DefaultChallengeScheme = "MultiAuth";
        })
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, _ => { })
        .AddScheme<ServiceTokenAuthenticationOptions, ServiceTokenAuthenticationHandler>(
            ServiceTokenAuthenticationHandler.SchemeName, options =>
            {
                options.ServiceToken = builder.Configuration["ServiceAuth:ServiceToken"] ?? "";
            })
        .AddPolicyScheme("MultiAuth", "ApiKey or ServiceToken", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (context.Request.Headers.ContainsKey("X-Service-Token"))
                    return ServiceTokenAuthenticationHandler.SchemeName;

                return ApiKeyAuthenticationHandler.SchemeName;
            };
        });

    builder.Services.AddAuthorization();

    // ─── Manejador de Excepciones ───────────────────────────────────────────
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // ─── Swagger / OpenAPI ───────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "SriYa API",
            Version = "v1",
            Description = "Ecuadorian electronic invoicing microservice"
        });

        options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "X-Api-Key",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Description = "API key for tenant authentication"
        });

        options.AddSecurityDefinition("ServiceToken", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "X-Service-Token",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Description = "Service token for internal service-to-service calls"
        });

        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            },
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "ServiceToken"
                    }
                },
                Array.Empty<string>()
            }
        });

        // ─── Encabezado opcional X-Tenant-Id (necesario para llamadas con service-token) ───
        options.OperationFilter<Qora.Billing.Api.Swagger.TenantIdHeaderFilter>();
    });

    // ─── CORS ────────────────────────────────────────────────────────
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:3000"];
            policy.WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
    });

    // ─── Rate Limiting (limitación de solicitudes) ───────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddSlidingWindowLimiter("api-key-policy", limiterOptions =>
        {
            limiterOptions.PermitLimit = builder.Configuration.GetValue<int>("RateLimit:PermitLimit", 120);
            limiterOptions.Window = TimeSpan.FromSeconds(
                builder.Configuration.GetValue<int>("RateLimit:WindowSeconds", 60));
            limiterOptions.SegmentsPerWindow = 6;
            limiterOptions.QueueLimit = 0;
            limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        });

        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)TimeSpan.FromSeconds(
                    context.HttpContext.RequestServices
                        .GetRequiredService<IConfiguration>()
                        .GetValue<int>("RateLimit:WindowSeconds", 60)).TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsJsonAsync(
                new
                {
                    type = "https://tools.ietf.org/html/rfc6585#section-4",
                    title = "Demasiadas solicitudes",
                    status = 429,
                    detail = "Límite de solicitudes excedido. Intente nuevamente en unos segundos."
                },
                cancellationToken);
        };
    });

    // ─── Health Checks (chequeos de salud) ───────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<Qora.Billing.Infrastructure.Persistence.BillingDbContext>("database");

    // ═══════════════════════════════════════════════════════════════════
    var app = builder.Build();
    // ═══════════════════════════════════════════════════════════════════

    // ─── Producción: protección contra clave débil ─────────────────────────────────
    if (app.Environment.IsProduction())
    {
        var encryptionKey = builder.Configuration["Encryption:Key"];
        if (string.IsNullOrEmpty(encryptionKey) || encryptionKey == "change-this-32-char-key-in-prod!!")
            throw new InvalidOperationException(
                "Encryption:Key no puede ser el valor por defecto en ambiente Production.");
    }

    // ─── Provisión de roles de BD (ANTES de migrar) ─────────────────────────────────────
    // Crea/actualiza de forma idempotente billing_app (NO BYPASSRLS) y billing_privileged (BYPASSRLS)
    // como el rol propietario/bootstrap, ANTES de MigrateAsync, para que las migraciones que activan
    // FORCE RLS y conceden privilegios encuentren los roles existentes. Mantiene la instalación Docker
    // de un solo comando. Se omite en el ambiente de pruebas (InMemory, sin Postgres real).
    if (!app.Environment.IsEnvironment("Testing"))
    {
        using var roleScope = app.Services.CreateScope();
        var roleProvisioner = new Qora.Billing.Infrastructure.Persistence.RoleProvisioner(
            builder.Configuration,
            roleScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Qora.Billing.Infrastructure.Persistence.RoleProvisioner>>());
        await roleProvisioner.ProvisionAsync();
        Log.Information("Database roles provisioned (billing_app, billing_privileged)");
    }

    // ─── Migración automática de la Base de Datos ───────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        // En ejecución normal, el DbContext por defecto se conecta como billing_app (NO propietario),
        // que NO puede ejecutar DDL ni activar FORCE RLS. Las migraciones deben correr como el rol
        // propietario/bootstrap (la connection string base), así que construimos un contexto dedicado
        // para migrar. En el ambiente de pruebas (InMemory) usamos el contexto del contenedor tal cual.
        if (app.Environment.IsEnvironment("Testing"))
        {
            // El proveedor InMemory no soporta migraciones relacionales; crea el esquema bajo demanda.
            // EnsureCreated es un no-op seguro para InMemory y evita el error "Relational-specific methods".
            var testDb = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            await testDb.Database.EnsureCreatedAsync();
        }
        else
        {
            var ownerConnectionString = builder.Configuration.GetConnectionString("BillingDb")
                ?? throw new InvalidOperationException("ConnectionStrings:BillingDb es requerido.");
            var migrationOptions = new DbContextOptionsBuilder<BillingDbContext>()
                .UseNpgsql(ownerConnectionString,
                    npgsql => npgsql.MigrationsAssembly(typeof(BillingDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.SkippedEntityTypeConfigurationWarning))
                .Options;

            await using var migrationDb = new BillingDbContext(migrationOptions, builder.Configuration);

            var pending = await migrationDb.Database.GetPendingMigrationsAsync();
            if (pending.Any())
            {
                Log.Information("Applying {Count} pending migration(s): {Migrations}",
                    pending.Count(), string.Join(", ", pending));
            }
            await migrationDb.Database.MigrateAsync();
            Log.Information("Database migration completed successfully");

            // ─── B1d: Re-cifrar las filas existentes de electronic_signatures ────────────
            // Maneja la migración de datos que no puede ejecutarse dentro de la propia migración de EF
            // (AES+HKDF requiere APIs de criptografía de .NET no disponibles en PL/pgSQL).
            // Idempotente: las filas que ya están en formato Base64 cifrado con HKDF se omiten.
            // Corre como propietario para no chocar con RLS.
            var certMigrator = new Qora.Billing.Infrastructure.Persistence.CertificateDataMigrator(
                migrationDb,
                builder.Configuration,
                scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Qora.Billing.Infrastructure.Persistence.CertificateDataMigrator>>());
            await certMigrator.MigrateAsync();
        }
    }

    // ─── Pipeline de Middleware ─────────────────────────────────────────
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "SriYa API v1");
        });
    }

    app.UseCors();
    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    app.UseTenantContext();

    // ─── Mapeo de Endpoints ───────────────────────────────────────────────
    app.MapDocumentEndpoints().RequireRateLimiting("api-key-policy");
    app.MapTenantEndpoints();
    app.MapCertificateEndpoints().RequireRateLimiting("api-key-policy");
    app.MapApiKeyEndpoints().RequireRateLimiting("api-key-policy");
    app.MapEmailEndpoints();
    app.MapLifecycleEndpoints().RequireRateLimiting("api-key-policy");
    app.MapBootstrapEndpoints().RequireRateLimiting("api-key-policy");
    app.MapHealthEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Requerido por WebApplicationFactory en las pruebas de integración
public partial class Program;
