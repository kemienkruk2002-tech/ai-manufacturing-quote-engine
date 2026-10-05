using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace QuoteEngine.IntegrationTests;

public sealed class TenantRouteAuthorizationMetadataTests
{
    private const string OfflineConnection =
        "Host=127.0.0.1;Port=1;Database=route_metadata_unused;Username=route_metadata_unused;Password=route_metadata_unused;Timeout=1";

    [Fact]
    public async Task Every_tenant_route_requires_tenant_policy_and_public_system_routes_remain_public()
    {
        await using var factory = new ApiFactory(OfflineConnection, "Production", false);
        using var client = factory.CreateClient();

        using var healthResponse = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, healthResponse.StatusCode);

        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var tenantEndpoints = endpoints
            .Where(endpoint => Normalize(endpoint.RoutePattern.RawText)
                .StartsWith("/api/tenants/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(tenantEndpoints);
        foreach (var endpoint in tenantEndpoints)
        {
            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(authorization, item =>
                string.Equals(item.Policy, "TenantRfqAccess", StringComparison.Ordinal));
        }

        AssertPublic(endpoints, "/health");
        AssertPublic(endpoints, "/openapi/v1.json");
    }

    private static void AssertPublic(IEnumerable<RouteEndpoint> endpoints, string path)
    {
        var endpoint = Assert.Single(endpoints.Where(item =>
            string.Equals(Normalize(item.RoutePattern.RawText), path, StringComparison.Ordinal)));
        Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    private static string Normalize(string? route) => "/" + (route ?? string.Empty).TrimStart('/');
}
