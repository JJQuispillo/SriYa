using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Logging;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.BackgroundServices;

/// <summary>
/// Red de seguridad del flujo de emisión atómica (cambio sri-emision-atomicidad, design D4/D8).
/// Barre periódicamente los documentos en estado <c>SentToSri</c> obsoletos (más viejos que
/// <c>StaleSentToSriAfterSeconds</c>) y re-consulta su autorización con el SRI, de modo que un
/// comprobante que el SRI recibió pero cuya autorización nunca se persistió (proceso muerto entre
/// el envío y la verificación) sea eventualmente reconciliado.
/// </summary>
/// <remarks>
/// <para>
/// Coordinación cross-pod por <c>FOR UPDATE SKIP LOCKED</c> (design D4): cada pod reclama un lote
/// distinto de documentos atómicamente, sin necesidad de leader election. El reconciliador es
/// INCONDICIONAL (design D9.a): es la red de seguridad del bug N1; apagarlo escondería el bug
/// que defiende. Para detenerlo
/// efectivamente, poner <see cref="SriReconciliationOptions.SweepIntervalSeconds"/> en 86400 o mayor.
/// </para>
/// <para>
/// Manejo de errores por documento (design D8): una falla al re-verificar un documento se loguea como
/// Warning y se OMITE; el documento permanece en <c>SentToSri</c> y el próximo barrido lo reintenta.
/// Un error a nivel de barrido NO tumba el BackgroundService (catch en el loop).
/// </para>
/// </remarks>
public class SriReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SriReconciliationOptions _options;
    private readonly ILogger<SriReconciliationService> _logger;

    public SriReconciliationService(
        IServiceScopeFactory scopeFactory,
        IOptions<SriReconciliationOptions> options,
        ILogger<SriReconciliationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SRI Reconciliation Service started (SweepInterval={SweepIntervalSeconds}s, StaleAfter={StaleSentToSriAfterSeconds}s, MaxBatch={MaxBatchSize})",
            _options.SweepIntervalSeconds, _options.StaleSentToSriAfterSeconds, _options.MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in SRI Reconciliation Service sweep");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.SweepIntervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break; // esperado en shutdown
            }
        }

        _logger.LogInformation("SRI Reconciliation Service stopped");
    }

    /// <summary>
    /// Orquestación de un barrido: reclama un lote de documentos obsoletos (con SKIP LOCKED) y los
    /// reconcilia uno a uno, continuando ante errores por documento (design D8).
    /// </summary>
    internal async Task ReconcileSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var documentRepository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sriClient = scope.ServiceProvider.GetRequiredService<ISriClient>();

        var olderThan = DateTime.UtcNow.AddSeconds(-_options.StaleSentToSriAfterSeconds);
        var staleDocuments = await documentRepository.GetStaleSentToSriAsync(
            olderThan, _options.MaxBatchSize, cancellationToken);

        if (staleDocuments.Count == 0)
            return;

        _logger.LogEmissionReconciliationSweep(staleDocuments.Count);

        foreach (var document in staleDocuments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await ReconcileDocumentAsync(document, documentRepository, unitOfWork, sriClient, cancellationToken);
            }
            catch (Exception ex)
            {
                // D8: log Warning + omitir + siguiente. El documento queda en SentToSri; el próximo
                // barrido lo reintenta.
                _logger.LogEmissionReconciliationDocError(ex, document.Id, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Reconcilia un documento: re-consulta su autorización con el SRI y persiste el estado resultante.
    /// Autorizado → <c>Authorize</c>; aún no autorizado → <c>ScheduleRetry</c> (lo recoge el retry
    /// service). El estado se persiste con el patrón stage+save (UpdateAsync + SaveChangesAsync).
    /// </summary>
    internal async Task ReconcileDocumentAsync(
        Document document,
        IDocumentRepository documentRepository,
        IUnitOfWork unitOfWork,
        ISriClient sriClient,
        CancellationToken cancellationToken)
    {
        var authResult = await sriClient.CheckAuthorizationAsync(document.AccessKey!.Value, cancellationToken);

        if (authResult.IsAuthorized && authResult.AuthorizationNumber is not null && authResult.AuthorizationDate.HasValue)
        {
            document.Authorize(authResult.AuthorizationNumber, authResult.AuthorizationDate.Value);
            await documentRepository.UpdateAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogEmissionReconciledAuthorized(document.Id, authResult.AuthorizationNumber);
        }
        else
        {
            // SRI aún no autoriza → programa reintento para que el retry service lo siga.
            document.ScheduleRetry(DateTime.UtcNow.AddMinutes(5));
            await documentRepository.UpdateAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogEmissionReconciledPendingRetry(document.Id);
        }
    }
}
