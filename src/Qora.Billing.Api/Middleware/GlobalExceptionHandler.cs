using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Global exception handler implementing IExceptionHandler (.NET 8+).
/// Maps domain exceptions to appropriate HTTP status codes with RFC 7807 ProblemDetails.
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
                Title = "Document Validation Failed",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                Extensions = { ["errors"] = ex.Errors }
            },

            ValidationException ex => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Validation Failed",
                Detail = "One or more validation errors occurred.",
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
                Title = "Certificate Expired",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            TenantInactiveException ex => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Tenant Inactive",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3"
            },

            InvalidAccessKeyException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Access Key",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            InvalidRucException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid RUC",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            BillingDomainException ex => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Billing Error",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            },

            KeyNotFoundException ex => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Resource Not Found",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
            },

            UnauthorizedAccessException ex => new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = ex.Message,
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            },

            HttpRequestException ex => new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "SRI Service Unavailable",
                Detail = $"The SRI external service is unreachable or returned an error: {ex.Message}",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.3",
                Extensions = { ["sriError"] = ex.Message }
            },

            TaskCanceledException ex when ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested => new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "SRI Service Timeout",
                Detail = "The request to the SRI external service timed out.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.5",
                Extensions = { ["sriError"] = "Request timed out waiting for SRI response." }
            },

            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred. Please try again later.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
            }
        };

        // Add traceId to all ProblemDetails for correlation
        var traceId = System.Diagnostics.Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["traceId"] = traceId;

        // Log based on severity — never log sensitive data
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
