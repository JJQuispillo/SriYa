using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Qora.Billing.Api.Middleware;

/// <summary>
/// Manejador de autenticación para llamadas internas entre servicios.
/// Valida el encabezado X-Service-Token contra un token de servicio configurado.
/// Usado por la API principal del POS para gestionar tenants y otras operaciones administrativas.
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
/// Opciones para la autenticación mediante token de servicio.
/// </summary>
public class ServiceTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// El valor esperado del token de servicio para la autenticación interna entre servicios.
    /// </summary>
    public string ServiceToken { get; set; } = string.Empty;
}
