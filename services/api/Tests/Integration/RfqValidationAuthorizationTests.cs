using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.IntegrationTests;

public sealed class RfqValidationAuthorizationTests
{
    private static readonly Guid TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RfqId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Unauthenticated_invalid_input_is_rejected_before_repository_access(string method)
    {
        var repository = new CountingQuoteRequestRepository();
        await using var factory = new RfqValidationAuthorizationFactory(repository);
        using var client = factory.CreateClient();

        using var response = await SendInvalidAsync(client, method, TenantId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Missing_tenant_claim_is_rejected_before_repository_access(string method)
    {
        var repository = new CountingQuoteRequestRepository();
        await using var factory = new RfqValidationAuthorizationFactory(repository);
        using var client = factory.CreateClient();
        TestAuthentication.AuthenticateWithoutTenant(client);

        using var response = await SendInvalidAsync(client, method, TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Cross_tenant_invalid_input_is_hidden_before_repository_access(string method)
    {
        var repository = new CountingQuoteRequestRepository();
        await using var factory = new RfqValidationAuthorizationFactory(repository);
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, TenantId);

        using var response = await SendInvalidAsync(client, method, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, repository.TotalCalls);
    }

    private static async Task<HttpResponseMessage> SendInvalidAsync(
        HttpClient client, string method, Guid routeTenantId)
    {
        var url = $"/api/tenants/{routeTenantId}/rfqs";
        if (method == "PUT") url += $"/{RfqId}/draft";
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = JsonContent.Create(new
            {
                currency = "pln",
                requestedQuantity = 0,
                expectedRowVersion = 1
            })
        };
        return await client.SendAsync(request);
    }
}

internal sealed class RfqValidationAuthorizationFactory(CountingQuoteRequestRepository repository)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuoteEngine"] =
                    "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
                ["Dev:GoldenEnabled"] = "false"
            }));
        builder.ConfigureTestServices(services =>
        {
            TestAuthentication.Configure(services);
            services.RemoveAll<IQuoteRequestRepository>();
            services.AddSingleton<IQuoteRequestRepository>(repository);
        });
    }
}

internal sealed class CountingQuoteRequestRepository : IQuoteRequestRepository
{
    private int calls;
    public int TotalCalls => Volatile.Read(ref calls);

    public Task<QuoteRequest> CreateAsync(QuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        throw new InvalidOperationException("Unexpected repository access.");
    }

    public Task<QuoteRequest?> FindAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        throw new InvalidOperationException("Unexpected repository access.");
    }

    public Task<IReadOnlyList<QuoteRequest>> ListAsync(Guid tenantId, QuoteRequestFilter filter,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        throw new InvalidOperationException("Unexpected repository access.");
    }

    public Task<QuoteRequest?> UpdateDraftAsync(Guid tenantId, Guid quoteRequestId, QuoteDraftUpdate update,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        throw new InvalidOperationException("Unexpected repository access.");
    }
}
