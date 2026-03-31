using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qora.Billing.Api.Swagger;

/// <summary>
/// Adds an optional X-Tenant-Id header parameter to every Swagger operation.
/// This header is required when using service-token authentication to specify
/// which tenant the operation targets (e.g., creating API keys).
/// </summary>
public class TenantIdHeaderFilter : IOperationFilter
{
    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/tenants",
        "/health",
        "/health/ready"
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath;
        if (path is not null && ExcludedPaths.Contains("/" + path))
            return;

        operation.Parameters ??= new List<OpenApiParameter>();

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Tenant-Id",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Tenant ID (used with X-Service-Token authentication to specify target tenant)",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Format = "uuid"
            }
        });
    }
}
