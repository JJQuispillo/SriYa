using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.BackgroundServices;

/// <summary>
/// Servicio en segundo plano que periódicamente consulta (polling) los documentos con estado PendingRetry
/// y los reenvía al SRI. Usa exponential backoff con un tope de 4 horas.
/// </summary>
public class SriRetryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SriRetryService> _logger;

    /// <summary>
    /// Intervalo de polling: con qué frecuencia el servicio revisa si hay documentos listos para reintentar.
    /// </summary>
    internal static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Número máximo de intentos de reintento antes de marcar un documento como Failed.
    /// </summary>
    internal const int MaxRetries = 10;

    /// <summary>
    /// Retardo base para el exponential backoff (5 minutos).
    /// </summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tope máximo de retardo para el exponential backoff (4 horas).
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(4);

    public SriRetryService(
        IServiceScopeFactory scopeFactory,
        ILogger<SriRetryService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SRI Retry Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in SRI Retry Service polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("SRI Retry Service stopped");
    }

    internal async Task ProcessPendingRetriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var documentRepository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var sriClient = scope.ServiceProvider.GetRequiredService<ISriClient>();

        var pendingDocuments = await documentRepository.GetPendingRetryAsync(
            DateTime.UtcNow, maxResults: 100, cancellationToken);

        if (pendingDocuments.Count == 0)
            return;

        _logger.LogInformation("Found {Count} documents pending retry", pendingDocuments.Count);

        foreach (var document in pendingDocuments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await RetryDocumentAsync(document, documentRepository, sriClient, cancellationToken);
        }
    }

    private async Task RetryDocumentAsync(
        Domain.Entities.Document document,
        IDocumentRepository documentRepository,
        ISriClient sriClient,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Retrying document {DocumentId}, attempt {RetryCount}/{MaxRetries}",
            document.Id, document.RetryCount + 1, MaxRetries);

        try
        {
            // Reenviar el XML firmado al SRI
            var sendResult = await sriClient.SendDocumentAsync(
                document.SignedXmlContent!, cancellationToken);

            if (!sendResult.IsAccepted)
            {
                // El SRI devolvió DEVUELTA: el documento tiene un error de contenido.
                // Según la Ficha Técnica del SRI §5.10, reenviar el mismo XML firmado siempre fallará.
                // Es una falla permanente que requiere corrección humana — NO programar reintentos.
                var sriError = string.Join("; ", sendResult.Messages);
                var errorMsg = $"SRI rechazó el documento (DEVUELTA): {sriError}. Se requiere corrección manual antes de reenviar.";
                _logger.LogWarning(
                    "Document {DocumentId} permanently rejected by SRI (DEVUELTA) on retry attempt {RetryCount}: {Error}",
                    document.Id, document.RetryCount + 1, sriError);
                document.Reject(errorMsg);
                document.MarkFailed(errorMsg);
                await documentRepository.UpdateAsync(document, cancellationToken);
                return;
            }

            // Verificar el estado de autorización
            var authResult = await sriClient.CheckAuthorizationAsync(
                document.AccessKey!.Value, cancellationToken);

            if (authResult.IsAuthorized)
            {
                document.Authorize(authResult.AuthorizationNumber!, authResult.AuthorizationDate!.Value);
                _logger.LogInformation(
                    "Document {DocumentId} authorized on retry (attempt {RetryCount})",
                    document.Id, document.RetryCount);
            }
            else
            {
                HandleRetryFailure(document, string.Join("; ", authResult.Messages));
            }

            await documentRepository.UpdateAsync(document, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error during retry of document {DocumentId}, attempt {RetryCount}",
                document.Id, document.RetryCount + 1);

            HandleRetryFailure(document, ex.Message);
            await documentRepository.UpdateAsync(document, cancellationToken);
        }
    }

    private void HandleRetryFailure(Domain.Entities.Document document, string errorMessage)
    {
        // Transicionar primero a Rejected (requerido por la máquina de estados del dominio antes de ScheduleRetry)
        document.Reject(errorMessage);

        // Verificar si se superó el máximo de reintentos (RetryCount es el conteo actual antes del siguiente incremento de ScheduleRetry)
        if (document.RetryCount + 1 >= MaxRetries)
        {
            document.MarkFailed($"Se superó el máximo de reintentos ({MaxRetries}). Último error: {errorMessage}");
            _logger.LogWarning(
                "Document {DocumentId} marked as Failed after {MaxRetries} retries",
                document.Id, MaxRetries);
        }
        else
        {
            var nextRetryAt = CalculateNextRetryTime(document.RetryCount);
            document.ScheduleRetry(nextRetryAt);
            _logger.LogInformation(
                "Document {DocumentId} scheduled for retry at {NextRetryAt} (attempt {RetryCount})",
                document.Id, nextRetryAt, document.RetryCount);
        }
    }

    /// <summary>
    /// Calcula el siguiente momento de reintento usando exponential backoff.
    /// Patrón: 5min, 10min, 20min, 40min, 1h20m, 2h40m, 4h (tope).
    /// Fórmula: min(BaseDelay * 2^retryCount, MaxDelay)
    /// </summary>
    internal static DateTime CalculateNextRetryTime(int currentRetryCount)
    {
        var delay = CalculateBackoffDelay(currentRetryCount);
        return DateTime.UtcNow.Add(delay);
    }

    /// <summary>
    /// Calcula el retardo de backoff para un conteo de reintentos dado.
    /// Expuesto internamente para pruebas.
    /// </summary>
    internal static TimeSpan CalculateBackoffDelay(int currentRetryCount)
    {
        // 2^retryCount * BaseDelay, limitado a MaxDelay
        var multiplier = Math.Pow(2, currentRetryCount);
        var delayMinutes = BaseDelay.TotalMinutes * multiplier;
        var delay = TimeSpan.FromMinutes(Math.Min(delayMinutes, MaxDelay.TotalMinutes));
        return delay;
    }
}
