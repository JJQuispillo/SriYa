namespace Qora.Billing.Application.DTOs;

public record CreateApiKeyRequest(
    string Nombre,
    DateTime? FechaExpiracion = null);

public record ApiKeyResponse(
    Guid Id,
    string Nombre,
    string? Clave,
    bool Activo,
    DateTime? FechaExpiracion,
    DateTime FechaCreacion);
