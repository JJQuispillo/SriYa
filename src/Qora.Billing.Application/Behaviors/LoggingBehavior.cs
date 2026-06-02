using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Qora.Billing.Application.Behaviors;

/// <summary>
/// Behavior del pipeline de MediatR que registra la solicitud/respuesta.
/// NUNCA registra datos sensibles (certificados, contraseñas, API keys).
/// </summary>
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("[MediatR] Handling {RequestName}", requestName);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await next(cancellationToken);
            stopwatch.Stop();

            _logger.LogInformation(
                "[MediatR] Handled {RequestName} in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(ex,
                "[MediatR] {RequestName} failed after {ElapsedMs}ms: {ErrorMessage}",
                requestName, stopwatch.ElapsedMilliseconds, ex.Message);

            throw;
        }
    }
}
