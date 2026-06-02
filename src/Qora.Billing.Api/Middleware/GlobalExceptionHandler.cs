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
                        .Select(e => new { e.PropertyName, e.ErrorMessage })
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
        if (problemDetails.Status >= 500)
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
}
