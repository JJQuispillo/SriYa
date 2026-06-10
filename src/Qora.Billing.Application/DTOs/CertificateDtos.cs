namespace Qora.Billing.Application.DTOs;

public record CertificateResponse(
    Guid Id,
    string NombrePropietario,
    DateTime FechaExpiracion,
    bool Activo,
    DateTime FechaCreacion);
