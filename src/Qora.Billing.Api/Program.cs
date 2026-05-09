using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Qora.Billing.Api.Endpoints;
using Qora.Billing.Api.Middleware;
using Qora.Billing.Application;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Infrastructure;
using Qora.Billing.Infrastructure.Persistence;
using Serilog;

// ─── Bootstrap Serilog ───────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ─────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) =>
        configuration.ReadFrom.Configuration(context.Configuration));

    // ─── Application Layer ───────────────────────────────────────────
    builder.Services.AddApplicationServices();

    // ─── API Key Settings (environment-aware prefix) ──────────────
    builder.Services.Configure<Qora.Billing.Application.Settings.ApiKeySettings>(options =>
    {
        options.Environment = builder.Environment.IsProduction() ? "Production" : "Test";
    });

    // ─── Infrastructure Layer (DB, repos, XML, signing, SRI, PDF) ───
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // ─── Tenant Context (scoped per request) ─────────────────────────
    builder.Services.AddScoped<ITenantContext, TenantContext>();

    // ─── Authentication ──────────────────────────────────────────────
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

    // ─── Exception Handler ───────────────────────────────────────────
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // ─── Swagger / OpenAPI ───────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "Qora Billing API",
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

        // ─── X-Tenant-Id optional header (needed for service-token calls) ───
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

    // ─── Health Checks ───────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<Qora.Billing.Infrastructure.Persistence.BillingDbContext>("database");

    // ═══════════════════════════════════════════════════════════════════
    var app = builder.Build();
    // ═══════════════════════════════════════════════════════════════════

    // ─── Auto-migrate Database ───────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var pending = await db.Database.GetPendingMigrationsAsync();
        if (pending.Any())
        {
            Log.Information("Applying {Count} pending migration(s): {Migrations}",
                pending.Count(), string.Join(", ", pending));
        }
        await db.Database.MigrateAsync();
        Log.Information("Database migration completed successfully");
    }

    // ─── Middleware Pipeline ─────────────────────────────────────────
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Qora Billing API v1");
        });
    }

    app.UseCors();
    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseTenantContext();

    // ─── Map Endpoints ───────────────────────────────────────────────
    app.MapDocumentEndpoints();
    app.MapTenantEndpoints();
    app.MapCertificateEndpoints();
    app.MapApiKeyEndpoints();
    app.MapUsageEndpoints();
    app.MapEmailEndpoints();
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

// Required for WebApplicationFactory in integration tests
public partial class Program;
