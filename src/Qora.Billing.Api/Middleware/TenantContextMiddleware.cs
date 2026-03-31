using Qora.Billing.Application.Interfaces;
using Qora.Billing.Infrastructure.Persistence;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Middleware that extracts TenantId from the authenticated user's claims
/// and sets it on both ITenantContext (for application layer) and
/// BillingDbContext (for multi-tenant query filters).
/// </summary>
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;

    public TenantContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        BillingDbContext dbContext)
    {
        Guid? tenantId = null;

        // 1. Try to get TenantId from claims (set by ApiKey auth handler)
        var tenantIdClaim = context.User.FindFirst("TenantId")?.Value;
        if (!string.IsNullOrEmpty(tenantIdClaim) && Guid.TryParse(tenantIdClaim, out var claimTenantId))
        {
            tenantId = claimTenantId;
        }

        // 2. For ServiceToken auth, allow X-Tenant-Id header to specify the tenant
        //    This enables service-to-service calls to operate on behalf of a specific tenant.
        if (!tenantId.HasValue
            && context.User.Identity?.AuthenticationType == ServiceTokenAuthenticationHandler.SchemeName
            && context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantIdHeader)
            && Guid.TryParse(tenantIdHeader.FirstOrDefault(), out var headerTenantId))
        {
            tenantId = headerTenantId;
        }

        if (tenantId.HasValue)
        {
            tenantContext.SetTenantId(tenantId.Value);
            dbContext.SetTenantId(tenantId.Value);
        }

        await _next(context);
    }
}

/// <summary>
/// Extension method for registering the TenantContextMiddleware in the pipeline.
/// </summary>
public static class TenantContextMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
