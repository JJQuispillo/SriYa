using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qora.Billing.Api.Swagger;

/// <summary>
/// Agrega un parámetro de encabezado opcional X-Tenant-Id a cada operación de Swagger.
/// Este encabezado es requerido cuando se usa autenticación con service-token para especificar
/// a qué tenant apunta la operación (por ejemplo, al crear API keys).
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
