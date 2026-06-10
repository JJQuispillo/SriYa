using Microsoft.Extensions.Logging;

namespace Qora.Billing.Application.Logging;

/// <summary>
/// <see cref="EventId"/> y métodos de extensión de logging para los eventos del flujo de emisión
/// (cambio sri-emision-atomicidad). Convención de IDs:
/// <list type="bullet">
///   <item>2001-2009 → handler (<c>ProcessDocumentCommandHandler</c>)</item>
///   <item>2010-2019 → retry service (<c>SriRetryService</c>)</item>
///   <item>2020-2029 → reconciliador (<c>SriReconciliationService</c>)</item>
///   <item>2030-2039 → reintento de RIDE PDF (<c>RidePdfRetryService</c>)</item>
/// </list>
/// Todos los métodos usan plantillas de mensaje estilo Serilog (sin interpolación de strings).
/// </summary>
public static class EmissionEvents
{
    // ── Handler (2001-2009) ────────────────────────────────────────────────────────────────
    public static readonly EventId EmissionPreReserved = new(2001, nameof(EmissionPreReserved));
    public static readonly EventId EmissionPreReservationDuplicate = new(2002, nameof(EmissionPreReservationDuplicate));
    public static readonly EventId EmissionSentToSriPersisted = new(2003, nameof(EmissionSentToSriPersisted));
    public static readonly EventId EmissionMonotonicityViolation = new(2004, nameof(EmissionMonotonicityViolation));
    public static readonly EventId EmissionCircuitOpenCaught = new(2005, nameof(EmissionCircuitOpenCaught));

    // ── Retry service (2010-2019) ──────────────────────────────────────────────────────────
    public static readonly EventId EmissionRetryPersisted = new(2010, nameof(EmissionRetryPersisted));
    public static readonly EventId EmissionCertExpiredDuringRetry = new(2011, nameof(EmissionCertExpiredDuringRetry));

    // ── Reconciliador (2020-2029) ──────────────────────────────────────────────────────────
    public static readonly EventId EmissionReconciliationSweep = new(2020, nameof(EmissionReconciliationSweep));
    public static readonly EventId EmissionReconciledAuthorized = new(2021, nameof(EmissionReconciledAuthorized));
    public static readonly EventId EmissionReconciledPendingRetry = new(2022, nameof(EmissionReconciledPendingRetry));
    public static readonly EventId EmissionReconciliationDocError = new(2023, nameof(EmissionReconciliationDocError));

    // ── RIDE PDF retry service (2030-2039) ──────────────────────────────────────────────────
    public static readonly EventId RidePdfRetrySweep = new(2030, nameof(RidePdfRetrySweep));
    public static readonly EventId RidePdfRegenerated = new(2031, nameof(RidePdfRegenerated));
    public static readonly EventId RidePdfRetryExhausted = new(2032, nameof(RidePdfRetryExhausted));
    public static readonly EventId RidePdfRetryDocError = new(2033, nameof(RidePdfRetryDocError));

    // ── Extension methods ──────────────────────────────────────────────────────────────────

    public static void LogEmissionPreReserved(this ILogger logger, Guid documentId, string secuencial) =>
        logger.LogInformation(EmissionPreReserved,
            "Emisión pre-reservada para documento {DocumentId} con secuencial {Secuencial}", documentId, secuencial);

    public static void LogEmissionPreReservationDuplicate(this ILogger logger, Guid documentId, string? secuencial) =>
        logger.LogInformation(EmissionPreReservationDuplicate,
            "Identidad de negocio duplicada en pre-reserva para documento {DocumentId} (secuencial {Secuencial}); se devuelve el existente",
            documentId, secuencial);

    public static void LogEmissionSentToSriPersisted(this ILogger logger, Guid documentId) =>
        logger.LogInformation(EmissionSentToSriPersisted,
            "Documento {DocumentId} persistido como SentToSri (checkpoint 4)", documentId);

    public static void LogEmissionMonotonicityViolation(this ILogger logger, Guid documentId, string expected, string actual) =>
        logger.LogWarning(EmissionMonotonicityViolation,
            "Violación de monotonicidad de secuencial para documento {DocumentId}: esperaba {Expected}, recibió {Actual}",
            documentId, expected, actual);

    public static void LogEmissionCircuitOpenCaught(this ILogger logger, Exception exception, Guid documentId, string exceptionType) =>
        logger.LogWarning(EmissionCircuitOpenCaught, exception,
            "Falla transitoria/circuito abierto del SRI capturada para documento {DocumentId} ({ExceptionType}); el reconciliador lo procesará",
            documentId, exceptionType);

    public static void LogEmissionRetryPersisted(this ILogger logger, string eventType, Guid documentId, int rowsAffected) =>
        logger.LogInformation(EmissionRetryPersisted,
            "SriRetry persisted state transition {EventType} for document {DocumentId} (rows affected: {Affected})",
            eventType, documentId, rowsAffected);

    public static void LogEmissionCertExpiredDuringRetry(this ILogger logger, Guid documentId, Guid tenantId) =>
        logger.LogWarning(EmissionCertExpiredDuringRetry,
            "Certificado expiró/inactivo durante reintento del documento {DocumentId} (tenant {TenantId}); marcado como Failed",
            documentId, tenantId);

    public static void LogEmissionReconciliationSweep(this ILogger logger, int staleCount) =>
        logger.LogInformation(EmissionReconciliationSweep,
            "Barrido de reconciliación: {StaleCount} documentos SentToSri obsoletos encontrados", staleCount);

    public static void LogEmissionReconciledAuthorized(this ILogger logger, Guid documentId, string authorizationNumber) =>
        logger.LogInformation(EmissionReconciledAuthorized,
            "Reconciliado documento {DocumentId} → Authorized (N° {AuthorizationNumber})", documentId, authorizationNumber);

    public static void LogEmissionReconciledPendingRetry(this ILogger logger, Guid documentId) =>
        logger.LogInformation(EmissionReconciledPendingRetry,
            "Reconciliado documento {DocumentId} → PendingRetry (SRI aún no autoriza)", documentId);

    public static void LogEmissionReconciliationDocError(this ILogger logger, Exception exception, Guid documentId, string exceptionType) =>
        logger.LogWarning(EmissionReconciliationDocError, exception,
            "Error reconciliando documento {DocumentId} ({ExceptionType}); se omite y continúa con el siguiente",
            documentId, exceptionType);

    public static void LogRidePdfRetrySweep(this ILogger logger, int pendingCount) =>
        logger.LogInformation(RidePdfRetrySweep,
            "Barrido de RIDE PDF: {PendingCount} documentos Authorized sin RIDE generado encontrados", pendingCount);

    public static void LogRidePdfRegenerated(this ILogger logger, Guid documentId, int attempt) =>
        logger.LogInformation(RidePdfRegenerated,
            "RIDE PDF regenerado para documento {DocumentId} (intento {Attempt})", documentId, attempt);

    public static void LogRidePdfRetryExhausted(this ILogger logger, Guid documentId, int maxRetries) =>
        logger.LogWarning(RidePdfRetryExhausted,
            "Documento {DocumentId} superó el máximo de reintentos de RIDE ({MaxRetries}); requiere atención manual",
            documentId, maxRetries);

    public static void LogRidePdfRetryDocError(this ILogger logger, Exception exception, Guid documentId, string exceptionType) =>
        logger.LogWarning(RidePdfRetryDocError, exception,
            "Error regenerando RIDE PDF del documento {DocumentId} ({ExceptionType}); se omite y continúa con el siguiente",
            documentId, exceptionType);
}
