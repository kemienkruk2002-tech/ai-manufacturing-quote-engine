using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QuoteEngine.IntegrationTests;

internal static class TestAuthentication
{
    public const string Scheme = "QuoteEngine.Test";
    public const string AuthenticatedHeader = "X-Test-Authenticated";
    public const string TenantHeader = "X-Test-Tenant-Id";

    public static void Configure(IServiceCollection services)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = Scheme;
                options.DefaultChallengeScheme = Scheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(Scheme, _ => { });
    }

    public static void Authenticate(HttpClient client, Guid tenantId)
    {
        client.DefaultRequestHeaders.Remove(AuthenticatedHeader);
        client.DefaultRequestHeaders.Remove(TenantHeader);
        client.DefaultRequestHeaders.Add(AuthenticatedHeader, "true");
        client.DefaultRequestHeaders.Add(TenantHeader, tenantId.ToString("D"));
    }

    public static void AuthenticateWithoutTenant(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(AuthenticatedHeader);
        client.DefaultRequestHeaders.Remove(TenantHeader);
        client.DefaultRequestHeaders.Add(AuthenticatedHeader, "true");
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestAuthentication.AuthenticatedHeader, out var authenticated)
            || authenticated.Count != 1
            || !string.Equals(authenticated[0], "true", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-user")
        };
        if (Request.Headers.TryGetValue(TestAuthentication.TenantHeader, out var tenantValues))
        {
            foreach (var value in tenantValues)
                claims.Add(new Claim("tenant_id", value ?? string.Empty));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
