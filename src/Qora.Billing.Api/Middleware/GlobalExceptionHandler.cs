using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Manejador global de excepciones que implementa IExceptionHandler (.NET 8+).
/// Mapea las excepciones del dominio a los códigos de estado HTTP apropiados con ProblemDetails según RFC 7807.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception switch
        {
            DocumentValidationException ex => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Validación del documento fallida",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                Extensions = { ["errors"] = ex.Errors }
            },

            ValidationException ex => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Errores de validación",
                Detail = "Se produjeron uno o más errores de validación.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                Extensions =
                {
                    ["errors"] = ex.Errors
                        .Select(e => new { PropertyName = ToJsonPropertyName(e.PropertyName), e.ErrorMessage })
                        .ToList()
                }
            },

            CertificateExpiredException ex => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Certificado vencido",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            TenantInactiveException ex => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Tenant inactivo",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3"
            },

            InvalidAccessKeyException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Clave de acceso inválida",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            InvalidRucException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "RUC inválido",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            SriCircuitOpenException ex => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Servicio SRI no disponible (circuito abierto)",
                Detail = ex.Message,
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4",
                Extensions =
                {
                    // Garantiza un entero >= 1 para evitar Retry-After: 0 cuando BreakDuration=0
                    // (caso de IsolatedCircuitException manual, sin break automático).
                    ["retryAfterSeconds"] = Math.Max(1, (int)ex.BreakDuration.TotalSeconds),
                    ["reason"] = ex.Reason
                }
            },

            SecuencialExhaustedException ex => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "No se pudo asignar el secuencial",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8"
            },

            BillingDomainException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Error de facturación",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            KeyNotFoundException ex => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Recurso no encontrado",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
            },

            UnauthorizedAccessException ex => new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "No autorizado",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            },

            HttpRequestException ex => new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Servicio SRI no disponible",
                Detail = $"El servicio externo del SRI no está disponible o devolvió un error: {ex.Message}",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.3",
                Extensions = { ["sriError"] = ex.Message }
            },

            TaskCanceledException ex when ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested => new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "Tiempo de espera agotado con SRI",
                Detail = "La solicitud al servicio externo del SRI superó el tiempo de espera.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.5",
                Extensions = { ["sriError"] = "Se agotó el tiempo de espera de la respuesta del SRI." }
            },

            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Error interno del servidor",
                Detail = "Ocurrió un error inesperado. Por favor intente nuevamente más tarde.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
            }
        };

        // Agrega traceId a todos los ProblemDetails para correlación
        var traceId = System.Diagnostics.Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["traceId"] = traceId;

        // Registra según la severidad — nunca registrar datos sensibles
        if (problemDetails.Status == StatusCodes.Status503ServiceUnavailable)
        {
            // 503 por circuito abierto es una condición OPERACIONAL esperada, no un error del sistema.
            // Se loggea a Warning para no contaminar las alertas de errores 5xx reales.
            _logger.LogWarning(exception, "SRI circuit open: {Message}", exception.Message);
        }
        else if (problemDetails.Status >= 500)
        {
            _logger.LogError(exception, "Unhandled exception: {ExceptionType}", exception.GetType().Name);
        }
        else
        {
            _logger.LogWarning("Handled exception: {ExceptionType} — {Message}",
                exception.GetType().Name, exception.Message);
        }

        httpContext.Response.StatusCode = problemDetails.Status ?? 500;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }

    /// <summary>
    /// Normaliza el nombre de propiedad de FluentValidation para que coincida con el
    /// campo del body JSON: quita el prefijo del wrapper del command ("Request.") y
    /// pasa a camelCase cada segmento de la ruta separada por puntos.
    /// Ej.: "Request.Ruc" → "ruc"; "Emisor.Ruc" → "emisor.ruc";
    /// "Detalles[0].CodigoPorcentaje" → "detalles[0].codigoPorcentaje".
    /// </summary>
    private static string ToJsonPropertyName(string propertyName)
    {
        const string wrapperPrefix = "Request.";
        var name = propertyName.StartsWith(wrapperPrefix, StringComparison.Ordinal)
            ? propertyName[wrapperPrefix.Length..]
            : propertyName;

        if (name.Length == 0) return name;

        // camelCase cada segmento de la ruta (separado por '.'), preservando índices como "[0]".
        var segments = name.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        return string.Join('.', segments);
    }
}
