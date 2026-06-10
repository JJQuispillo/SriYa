using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Logging;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;

namespace Qora.Billing.Infrastructure.BackgroundServices;

/// <summary>
/// Servicio en segundo plano que periódicamente consulta (polling) los documentos con estado PendingRetry
/// y los reenvía al SRI. Usa exponential backoff con un tope configurable.
/// Todos los parámetros (intervalo, max retries, base/max delay) vienen de <see cref="SriRetryConfiguration"/>
/// inyectada vía <see cref="IOptions{TOptions}"/> (cambia sri-resiliencia-configuracion).
/// </summary>
/// <remarks>
/// FIX N1 (sri-emision-atomicidad REQ-EMI-018/T-EMI-001..003): el <c>IUnitOfWork</c> se resuelve
/// por scope (no por ctor) porque el servicio se registra como <see cref="IHostedService"/> (singleton)
/// y <c>IUnitOfWork</c> es scoped (depende del <c>DbContext</c>). Inyectarlo por ctor sería una
/// captive dependency. Cada iteración de <see cref="ProcessPendingRetriesAsync"/> crea su propio
/// scope y obtiene un <c>IUnitOfWork</c> fresco para garantizar que
/// <c>repo.UpdateAsync(...)</c> + <c>uow.SaveChangesAsync(...)</c> operen sobre el MISMO
/// <c>ChangeTracker</c>.
/// </remarks>
public class SriRetryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SriRetryService> _logger;
    private readonly SriRetryConfiguration _config;

    public SriRetryService(
        IServiceScopeFactory scopeFactory,
        IOptions<SriRetryConfiguration> retryOptions,
        ILogger<SriRetryService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(retryOptions);
        _config = retryOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SRI Retry Service started (PollingInterval={PollingIntervalSeconds}s, MaxRetries={MaxRetries}, BaseDelay={BaseDelaySeconds}s, MaxDelay={MaxDelaySeconds}s)",
            _config.PollingIntervalSeconds, _config.MaxRetries, _config.BaseDelaySeconds, _config.MaxDelaySeconds);

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

            await Task.Delay(TimeSpan.FromSeconds(_config.PollingIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("SRI Retry Service stopped");
    }

    internal async Task ProcessPendingRetriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var documentRepository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var sriClient = scope.ServiceProvider.GetRequiredService<ISriClient>();
        // FIX N1 (T-EMI-001): el IUnitOfWork es scoped y debe vivir en el mismo scope que
        // IDocumentRepository para que UpdateAsync y SaveChangesAsync operen sobre el mismo
        // ChangeTracker. Si no se resuelve por scope, sería captive dependency.
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        // T-EMI-022: el repo de firmas también se resuelve por scope (no por ctor) para re-verificar
        // el certificado en cada reintento (T-EMI-023).
        var signatureRepository = scope.ServiceProvider.GetRequiredService<IElectronicSignatureRepository>();

        var pendingDocuments = await documentRepository.GetPendingRetryAsync(
            DateTime.UtcNow, maxResults: 100, cancellationToken);

        if (pendingDocuments.Count == 0)
            return;

        _logger.LogInformation("Found {Count} documents pending retry", pendingDocuments.Count);

        foreach (var document in pendingDocuments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await RetryDocumentAsync(document, documentRepository, unitOfWork, signatureRepository, sriClient, cancellationToken);
        }
    }

    private async Task RetryDocumentAsync(
        Domain.Entities.Document document,
        IDocumentRepository documentRepository,
        IUnitOfWork unitOfWork,
        IElectronicSignatureRepository signatureRepository,
        ISriClient sriClient,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Retrying document {DocumentId}, attempt {RetryCount}/{MaxRetries}",
            document.Id, document.RetryCount + 1, _config.MaxRetries);

        // T-EMI-023 (REQ-EMI-016/017): re-verifica el certificado ANTES de reenviar. Un certificado
        // que venció o se desactivó entre la emisión y el reintento debe marcar el documento como
        // Failed (no reintentar indefinidamente con un cert inválido).
        var signature = await signatureRepository.GetActiveByTenantIdAsync(document.TenantId, cancellationToken);
        try
        {
            if (signature is null)
                throw new CertificateExpiredException(document.TenantId, DateTime.UtcNow);
            signature.EnsureValid();
        }
        catch (BillingDomainException)
        {
            document.MarkFailed("Certificado expiró durante reintento");
            await PersistAsync(document, documentRepository, unitOfWork, "FailedCertExpired", cancellationToken);
            _logger.LogEmissionCertExpiredDuringRetry(document.Id, document.TenantId);
            return;
        }

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
                await PersistAsync(document, documentRepository, unitOfWork, "FailedDevuelta", cancellationToken);
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
                await PersistAsync(document, documentRepository, unitOfWork, "Authorized", cancellationToken);
            }
            else
            {
                HandleRetryFailure(document, string.Join("; ", authResult.Messages));
                var eventType = document.Status == DocumentStatus.Failed ? "FailedMaxRetries" : "ScheduledRetry";
                await PersistAsync(document, documentRepository, unitOfWork, eventType, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error during retry of document {DocumentId}, attempt {RetryCount}",
                document.Id, document.RetryCount + 1);

            HandleRetryFailure(document, ex.Message);
            var eventType = document.Status == DocumentStatus.Failed ? "FailedMaxRetries" : "ScheduledRetry";
            await PersistAsync(document, documentRepository, unitOfWork, eventType, cancellationToken);
        }
    }

    /// <summary>
    /// Helper privado que stagea los cambios vía <see cref="IDocumentRepository.UpdateAsync"/> y
    /// luego flushea el <c>ChangeTracker</c> vía <see cref="IUnitOfWork.SaveChangesAsync"/>.
    /// Patrón stage-only + flush explícito: el repositorio stagea y la unit of work flushea, de
    /// modo que múltiples ciclos stage+save pueden ocurrir dentro de la misma transacción del
    /// scope (ver contrato en <see cref="IDocumentRepository.UpdateAsync"/> XML doc).
    /// </summary>
    /// <remarks>
    /// FIX N1 (T-EMI-002): el código previo sólo llamaba a <c>UpdateAsync</c> (stage-only) y NUNCA
    /// a <c>SaveChangesAsync</c>, perdiendo silenciosamente TODAS las transiciones de estado del
    /// retry service en producción durante meses. Este helper centraliza el patrón stage+save
    /// para que los 3 call sites de <see cref="RetryDocumentAsync"/> no puedan divergir.
    /// </remarks>
    /// <param name="document">Entidad de dominio mutada (transición de estado aplicada).</param>
    /// <param name="repo">Repositorio de documentos del mismo scope que el UoW.</param>
    /// <param name="uow">Unit of work del mismo scope que el repo.</param>
    /// <param name="eventType">Etiqueta corta del tipo de transición (Authorized, FailedDevuelta,
    /// FailedMaxRetries, ScheduledRetry) — se incluye en el log estructurado con
    /// <see cref="EmissionEvents.EmissionRetryPersisted"/> (EventId 2010) para la canary metric de PR #1.</param>
    /// <param name="ct">Token de cancelación propagado del caller.</param>
    private async Task PersistAsync(
        Domain.Entities.Document document,
        IDocumentRepository repo,
        IUnitOfWork uow,
        string eventType,
        CancellationToken ct)
    {
        // 1) Stage: marca la entidad como Modified en el ChangeTracker. NO emite UPDATE SQL.
        await repo.UpdateAsync(document, ct);
        // 2) Flush: emite el UPDATE. ESTA es la línea que faltaba en el bug N1.
        var affected = await uow.SaveChangesAsync(ct);
        // Canary metric: log estructurado con EmissionEvents.EmissionRetryPersisted (EventId 2010) por
        // cada SaveChangesAsync exitoso. Útil para verificar en producción que el fix está activo (count > 0).
        _logger.LogEmissionRetryPersisted(eventType, document.Id, affected);
    }

    private void HandleRetryFailure(Domain.Entities.Document document, string errorMessage)
    {
        // Transicionar primero a Rejected (requerido por la máquina de estados del dominio antes de ScheduleRetry)
        document.Reject(errorMessage);

        // Verificar si se superó el máximo de reintentos (RetryCount es el conteo actual antes del siguiente incremento de ScheduleRetry)
        if (document.RetryCount + 1 >= _config.MaxRetries)
        {
            document.MarkFailed($"Se superó el máximo de reintentos ({_config.MaxRetries}). Último error: {errorMessage}");
            _logger.LogWarning(
                "Document {DocumentId} marked as Failed after {MaxRetries} retries",
                document.Id, _config.MaxRetries);
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
    /// Patrón con defaults: 5min, 10min, 20min, 40min, 1h20m, 2h40m, 4h (tope).
    /// Fórmula: min(BaseDelay * 2^retryCount, MaxDelay)
    /// </summary>
    internal DateTime CalculateNextRetryTime(int currentRetryCount)
    {
        var delay = CalculateBackoffDelay(currentRetryCount);
        return DateTime.UtcNow.Add(delay);
    }

    /// <summary>
    /// Calcula el retardo de backoff para un conteo de reintentos dado.
    /// Lee <see cref="SriRetryConfiguration.BaseDelaySeconds"/> y <see cref="SriRetryConfiguration.MaxDelaySeconds"/>
    /// del config inyectado. Expuesto como internal para pruebas (InternalsVisibleTo).
    /// </summary>
    internal TimeSpan CalculateBackoffDelay(int currentRetryCount)
    {
        // 2^retryCount * BaseDelay, limitado a MaxDelay
        var baseDelay = TimeSpan.FromSeconds(_config.BaseDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(_config.MaxDelaySeconds);
        var multiplier = Math.Pow(2, currentRetryCount);
        var delayMinutes = baseDelay.TotalMinutes * multiplier;
        var delay = TimeSpan.FromMinutes(Math.Min(delayMinutes, maxDelay.TotalMinutes));
        return delay;
    }
}
