namespace Qora.Billing.Application.DTOs;

/// <summary>
/// Resultado del onboarding atómico de un emisor (OB-1). La clave de API se entrega EN CLARO
/// una ÚNICA vez aquí — nunca se podrá recuperar de nuevo (consistente con CreateApiKey).
/// </summary>
public record BootstrapTenantResponse(
    Guid TenantId,
    string Ruc,
    string RazonSocial,
    Guid CertificadoId,
    DateTime CertificadoExpiraEn,
    Guid ApiKeyId,
    string ApiKey,
    DateTime FechaCreacion);
