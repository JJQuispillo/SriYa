using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class CreateApiKeyCommandHandler : IRequestHandler<CreateApiKeyCommand, ApiKeyResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IApiKeyRepository _apiKeyRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ApiKeySettings _apiKeySettings;

    public CreateApiKeyCommandHandler(
        ITenantRepository tenantRepository,
        IApiKeyRepository apiKeyRepository,
        IUnitOfWork unitOfWork,
        IOptions<ApiKeySettings> apiKeySettings)
    {
        _tenantRepository = tenantRepository;
        _apiKeyRepository = apiKeyRepository;
        _unitOfWork = unitOfWork;
        _apiKeySettings = apiKeySettings.Value;
    }

    public async Task<ApiKeyResponse> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");
        tenant.EnsureActive();

        // Genera una API key aleatoria criptográficamente segura con un prefijo según el entorno
        var prefix = _apiKeySettings.Prefix;
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var randomPart = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        var plaintextKey = $"{prefix}{randomPart}";

        // Aplica hash a la key con SHA-256 para almacenarla — el texto plano NUNCA se almacena
        var keyHash = HashApiKey(plaintextKey);

        var apiKey = ApiKey.Create(
            command.TenantId,
            keyHash,
            command.Request.Nombre,
            command.Request.FechaExpiracion);

        await _apiKeyRepository.CreateAsync(apiKey, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Devuelve la key en texto plano SOLO en la creación — nunca se podrá recuperar de nuevo
        return new ApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            plaintextKey,
            apiKey.IsActive,
            apiKey.ExpiresAt,
            apiKey.CreatedAt);
    }

    public static string HashApiKey(string plainTextKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainTextKey));
        return Convert.ToHexStringLower(bytes);
    }
}
