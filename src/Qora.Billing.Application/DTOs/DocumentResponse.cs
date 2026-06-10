using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs;

public record DocumentResponse(
    Guid Id,
    Guid TenantId,
    DocumentType TipoDocumento,
    string? ClaveAcceso,
    DocumentStatus Estado,
    string? NumeroAutorizacion,
    DateTime? FechaAutorizacion,
    string? MensajeError,
    DateTime FechaCreacion,
    DateTime? FechaProcesamiento,
    string? Secuencial = null,
    string? Estab = null,
    string? PtoEmi = null);

public record DocumentEventResponse(
    EventType TipoEvento,
    string Descripcion,
    DateTime FechaOcurrencia);
