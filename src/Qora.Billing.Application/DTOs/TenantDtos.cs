namespace Qora.Billing.Application.DTOs;

public record CreateTenantRequest(
    string Ruc,
    string RazonSocial,
    string? NombreComercial = null);

public record UpdateTenantRequest(
    string RazonSocial,
    string? NombreComercial = null,
    bool? Activo = null,
    bool? AutoGenerateSecuencial = null);

public record TenantResponse(
    Guid Id,
    string Ruc,
    string RazonSocial,
    string? NombreComercial,
    bool Activo,
    DateTime FechaCreacion,
    bool AutoGenerateSecuencial = false);
