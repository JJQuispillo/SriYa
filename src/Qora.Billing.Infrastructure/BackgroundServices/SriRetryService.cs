using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that periodically polls for documents with PendingRetry status
/// and re-sends them to SRI. Uses exponential backoff with a cap of 4 hours.
/// </summary>
public class SriRetryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SriRetryService> _logger;

    /// <summary>
    /// Polling interval: how often the service checks for documents ready to retry.
    /// </summary>
    internal static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of retry attempts before marking a document as Failed.
    /// </summary>
    internal const int MaxRetries = 10;

    /// <summary>
    /// Base delay for exponential backoff (5 minutes).
    /// </summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum delay cap for exponential backoff (4 hours).
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
            // Re-send the signed XML to SRI
            var sendResult = await sriClient.SendDocumentAsync(
                document.SignedXmlContent!, cancellationToken);

            if (!sendResult.IsAccepted)
            {
                // SRI returned DEVUELTA: the document has a content error.
                // Per SRI Ficha Técnica §5.10, re-sending the same signed XML will always fail.
                // This is a permanent failure that requires human correction — do NOT schedule retries.
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

            // Check authorization status
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
        // Transition to Rejected first (required by domain state machine before ScheduleRetry)
        document.Reject(errorMessage);

        // Check if max retries exceeded (RetryCount is current count before the next ScheduleRetry increment)
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
    /// Calculates the next retry time using exponential backoff.
    /// Pattern: 5min, 10min, 20min, 40min, 1h20m, 2h40m, 4h (cap).
    /// Formula: min(BaseDelay * 2^retryCount, MaxDelay)
    /// </summary>
    internal static DateTime CalculateNextRetryTime(int currentRetryCount)
    {
        var delay = CalculateBackoffDelay(currentRetryCount);
        return DateTime.UtcNow.Add(delay);
    }

    /// <summary>
    /// Calculates the backoff delay for a given retry count.
    /// Exposed internally for testing.
    /// </summary>
    internal static TimeSpan CalculateBackoffDelay(int currentRetryCount)
    {
        // 2^retryCount * BaseDelay, capped at MaxDelay
        var multiplier = Math.Pow(2, currentRetryCount);
        var delayMinutes = BaseDelay.TotalMinutes * multiplier;
        var delay = TimeSpan.FromMinutes(Math.Min(delayMinutes, MaxDelay.TotalMinutes));
        return delay;
    }
}
