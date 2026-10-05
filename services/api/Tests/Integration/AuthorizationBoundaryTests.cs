using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.IntegrationTests;

public sealed class AuthorizationBoundaryTests
{
    private static readonly Guid TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RequestId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Unauthenticated_request_is_rejected_before_repository_access()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DownloadUrl(TenantId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    [Fact]
    public async Task Authenticated_identity_without_tenant_claim_is_forbidden_before_repository_access()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: true);
        using var client = factory.CreateClient();
        TestAuthentication.AuthenticateWithoutTenant(client);

        using var response = await client.GetAsync(DownloadUrl(TenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    [Fact]
    public async Task Cross_tenant_route_is_hidden_before_repository_access()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: true);
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, TenantId);

        using var response = await client.GetAsync(DownloadUrl(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TENANT_RESOURCE_NOT_FOUND", problem.GetProperty("code").GetString());
        Assert.Equal(0, repository.TotalCalls);
    }

    [Fact]
    public async Task Matching_authenticated_tenant_reaches_repository()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: true);
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, TenantId);

        using var response = await client.GetAsync(DownloadUrl(TenantId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, repository.FindVersionCalls);
    }

    [Fact]
    public async Task Development_scheme_accepts_explicit_dev_tenant_header()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: false,
            environment: "Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-Id", TenantId.ToString("D"));

        using var response = await client.GetAsync(DownloadUrl(TenantId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, repository.FindVersionCalls);
    }

    [Fact]
    public async Task Production_scheme_ignores_development_tenant_header()
    {
        var repository = new CountingRfqFileRepository();
        await using var factory = new AuthorizationApiFactory(repository, useTestAuthentication: false,
            environment: "Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-Id", TenantId.ToString("D"));

        using var response = await client.GetAsync(DownloadUrl(TenantId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    private static string DownloadUrl(Guid routeTenantId) =>
        $"/api/tenants/{routeTenantId}/rfqs/{RequestId}/files/drawing/versions/1";
}

internal sealed class AuthorizationApiFactory(
    CountingRfqFileRepository repository,
    bool useTestAuthentication,
    string environment = "Production") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuoteEngine"] =
                    "Host=127.0.0.1;Port=1;Database=authorization_unused;Username=authorization_unused;Password=authorization_unused;Timeout=1",
                ["Dev:GoldenEnabled"] = "false",
                ["RfqFiles:StorageRoot"] = Path.Combine(Path.GetTempPath(), "quoteengine-auth-tests"),
                ["RfqFiles:MaxUploadBytes"] = "1024",
                ["RfqFiles:AllowedMimeTypes:0"] = "text/plain"
            }));

        builder.ConfigureTestServices(services =>
        {
            if (useTestAuthentication)
                TestAuthentication.Configure(services);
            services.RemoveAll<IRfqFileRepository>();
            services.AddSingleton<IRfqFileRepository>(repository);
        });
    }
}

internal sealed class CountingRfqFileRepository : IRfqFileRepository
{
    private int requestExistsCalls;
    private int getOrCreateCalls;
    private int findVersionCalls;
    private int listVersionsCalls;

    public int FindVersionCalls => Volatile.Read(ref findVersionCalls);
    public int TotalCalls =>
        Volatile.Read(ref requestExistsCalls)
        + Volatile.Read(ref getOrCreateCalls)
        + Volatile.Read(ref findVersionCalls)
        + Volatile.Read(ref listVersionsCalls);

    public Task<bool> RequestExistsAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref requestExistsCalls);
        return Task.FromResult(false);
    }

    public Task<RfqFileVersion> GetOrCreateVersionAsync(RfqFileVersionInput input,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref getOrCreateCalls);
        throw new NotSupportedException();
    }

    public Task<RfqFileVersion?> FindVersionAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, int versionNo, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref findVersionCalls);
        return Task.FromResult<RfqFileVersion?>(null);
    }

    public Task<IReadOnlyList<RfqFileVersion>> ListVersionsAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref listVersionsCalls);
        return Task.FromResult<IReadOnlyList<RfqFileVersion>>([]);
    }
}
