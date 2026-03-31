using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Authentication handler for internal service-to-service calls.
/// Validates X-Service-Token header against a configured service token.
/// Used by the main POS API to manage tenants and other admin operations.
/// </summary>
public class ServiceTokenAuthenticationHandler : AuthenticationHandler<ServiceTokenAuthenticationOptions>
{
    public const string SchemeName = "ServiceToken";
    private const string ServiceTokenHeaderName = "X-Service-Token";

    public ServiceTokenAuthenticationHandler(
        IOptionsMonitor<ServiceTokenAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ServiceTokenHeaderName, out var tokenValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = tokenValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Service token is empty."));
        }

        if (!string.Equals(token, Options.ServiceToken, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid service token."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Role, "Service"),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Options for service token authentication.
/// </summary>
public class ServiceTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The expected service token value for internal service-to-service authentication.
    /// </summary>
    public string ServiceToken { get; set; } = string.Empty;
}
