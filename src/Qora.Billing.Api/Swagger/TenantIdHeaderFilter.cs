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
        // Normalizar: la RelativePath puede venir con/sin slash inicial o final
        // (p. ej. "api/v1/tenants/" para MapPost("/") sobre un grupo), por lo que
        // se compara sin slashes en los extremos para que la exclusión sea robusta.
        if (path is not null && ExcludedPaths.Contains("/" + path.Trim('/')))
            return;

        operation.Parameters ??= new List<OpenApiParameter>();

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Tenant-Id",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Opcional. Solo con autenticación X-Service-Token, para indicar sobre qué empresa (tenant) operar. " +
                          "Es el 'id' devuelto al crear la empresa. Déjalo vacío si usas X-Api-Key.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Format = "uuid"
            }
        });
    }
}
