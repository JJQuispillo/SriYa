using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Custom authentication handler that validates API keys via X-Api-Key header.
/// Hashes the provided key with SHA-256 and looks it up in the database.
/// On success, sets TenantId claim from the associated API key record.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string ApiKeyHeaderName = "X-Api-Key";

    private readonly IApiKeyRepository _apiKeyRepository;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository apiKeyRepository)
        : base(options, logger, encoder)
    {
        _apiKeyRepository = apiKeyRepository;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.Fail("API key is empty.");
        }

        // Hash the provided key with SHA-256
        var keyHash = HashApiKey(apiKey);

        // Look up in database
        var apiKeyEntity = await _apiKeyRepository.GetByKeyHashAsync(keyHash);

        if (apiKeyEntity is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (!apiKeyEntity.IsActive)
        {
            return AuthenticateResult.Fail("API key has been revoked.");
        }

        if (apiKeyEntity.ExpiresAt.HasValue && apiKeyEntity.ExpiresAt.Value < DateTime.UtcNow)
        {
            return AuthenticateResult.Fail("API key has expired.");
        }

        var claims = new[]
        {
            new Claim("TenantId", apiKeyEntity.TenantId.ToString()),
            new Claim(ClaimTypes.Name, apiKeyEntity.Name),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(bytes);
    }
}
