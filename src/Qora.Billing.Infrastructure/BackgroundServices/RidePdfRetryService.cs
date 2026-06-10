using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Logging;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.BackgroundServices;

/// <summary>
/// Red de seguridad de la generación del RIDE PDF. El RIDE se genera best-effort en la emisión
/// (<c>ProcessDocumentCommandHandler</c>, tras autorizar) dentro de un <c>try/catch</c> que TRAGA la
/// excepción con un Warning. Si esa generación falla, el documento queda autorizado pero con
/// <c>RideGeneratedAt IS NULL</c> y sin reintento. Este servicio barre periódicamente esos documentos
/// y regenera el RIDE, marcando <c>RideGeneratedAt</c> al tener éxito.
/// </summary>
/// <remarks>
/// <para>
/// El RIDE no se persiste (se genera on-demand vía <see cref="IRideGenerator"/> en la descarga, el
/// email y la exportación); por eso el "éxito" es la regeneración SIN excepción, que se materializa
/// como la marca <c>RideGeneratedAt</c>. Regenerar es idempotente (QuestPDF produce los bytes a partir
/// del estado del documento).
/// </para>
/// <para>
/// Coordinación cross-pod por <c>FOR UPDATE SKIP LOCKED</c> (mismo patrón que
/// <see cref="SriReconciliationService"/>): cada pod reclama un lote distinto atómicamente. El barrido
/// usa el contexto privilegiado (BYPASSRLS) vía <c>IDocumentRepository.GetAuthorizedMissingRidePdfAsync</c>.
/// </para>
/// <para>
/// Manejo de errores por documento: una falla al regenerar incrementa <c>RideRetryCount</c>, se loguea
/// como Warning y se OMITE; el próximo barrido lo reintenta hasta <c>MaxRetries</c>. Al agotar los
/// reintentos el documento deja de ser candidato (<c>ride_retry_count &gt;= MaxRetries</c>) y se loguea
/// como agotado. Un error a nivel de barrido NO tumba el BackgroundService (catch en el loop).
/// </para>
/// <para>
/// FIX N1 (captive dependency): el servicio se registra como <see cref="IHostedService"/> (singleton),
/// por lo que <c>IUnitOfWork</c>/<c>IDocumentRepository</c>/<c>IRideGenerator</c> (scoped) se resuelven
/// por scope en cada barrido, NO por constructor.
/// </para>
/// </remarks>
public class RidePdfRetryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RidePdfRetryOptions _options;
    private readonly ILogger<RidePdfRetryService> _logger;

    public RidePdfRetryService(
        IServiceScopeFactory scopeFactory,
        IOptions<RidePdfRetryOptions> options,
        ILogger<RidePdfRetryService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RIDE PDF Retry Service started (SweepInterval={SweepIntervalSeconds}s, StaleAfter={StaleAfterSeconds}s, MaxRetries={MaxRetries}, MaxBatch={MaxBatchSize})",
            _options.SweepIntervalSeconds, _options.StaleAfterSeconds, _options.MaxRetries, _options.MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in RIDE PDF Retry Service sweep");
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

        _logger.LogInformation("RIDE PDF Retry Service stopped");
    }

    /// <summary>
    /// Orquestación de un barrido: reclama un lote de documentos autorizados sin RIDE (con SKIP LOCKED)
    /// y los regenera uno a uno, continuando ante errores por documento.
    /// </summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var documentRepository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rideGenerator = scope.ServiceProvider.GetRequiredService<IRideGenerator>();

        var olderThan = DateTime.UtcNow.AddSeconds(-_options.StaleAfterSeconds);
        var pendingDocuments = await documentRepository.GetAuthorizedMissingRidePdfAsync(
            olderThan, _options.MaxRetries, _options.MaxBatchSize, cancellationToken);

        if (pendingDocuments.Count == 0)
            return;

        _logger.LogRidePdfRetrySweep(pendingDocuments.Count);

        foreach (var document in pendingDocuments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await RegenerateRideAsync(document, documentRepository, unitOfWork, rideGenerator, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log Warning + omitir + siguiente. El documento queda Authorized sin RIDE; el próximo
                // barrido lo reintenta (hasta MaxRetries).
                _logger.LogRidePdfRetryDocError(ex, document.Id, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Regenera el RIDE PDF de un documento. Éxito → marca <c>RideGeneratedAt</c>; fallo → incrementa
    /// <c>RideRetryCount</c> y relanza (el caller lo captura por-documento). En ambos casos el estado se
    /// persiste con el patrón stage+save (UpdateAsync + SaveChangesAsync).
    /// </summary>
    internal async Task RegenerateRideAsync(
        Document document,
        IDocumentRepository documentRepository,
        IUnitOfWork unitOfWork,
        IRideGenerator rideGenerator,
        CancellationToken cancellationToken)
    {
        // Guarda defensiva: solo regeneramos RIDE de documentos autorizados (la query ya filtra por esto,
        // pero el contrato de MarkRideGenerated lo exige).
        if (document.Status is not DocumentStatus.Authorized)
            return;

        var attempt = document.RideRetryCount + 1;
        try
        {
            // Genera el RIDE; los bytes no se persisten (RIDE on-demand). El éxito = generar sin excepción.
            _ = await rideGenerator.GeneratePdfAsync(document, cancellationToken);

            document.MarkRideGenerated();
            await documentRepository.UpdateAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogRidePdfRegenerated(document.Id, attempt);
        }
        catch (Exception)
        {
            // Incrementa el contador (acota el reintento) y persiste, luego relanza para que el caller
            // lo loguee por-documento y continúe con el siguiente.
            document.IncrementRideRetryCount();
            await documentRepository.UpdateAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            if (document.RideRetryCount >= _options.MaxRetries)
                _logger.LogRidePdfRetryExhausted(document.Id, _options.MaxRetries);

            throw;
        }
    }
}
