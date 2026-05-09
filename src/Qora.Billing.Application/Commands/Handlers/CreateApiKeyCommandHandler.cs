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

        // Generate a cryptographically secure random API key with environment-aware prefix
        var prefix = _apiKeySettings.Prefix;
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var randomPart = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        var plaintextKey = $"{prefix}{randomPart}";

        // Hash the key with SHA-256 for storage — plaintext is NEVER stored
        var keyHash = HashApiKey(plaintextKey);

        var apiKey = ApiKey.Create(
            command.TenantId,
            keyHash,
            command.Request.Name,
            command.Request.ExpiresAt);

        await _apiKeyRepository.CreateAsync(apiKey, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Return plaintext key ONLY on creation — it will never be retrievable again
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
