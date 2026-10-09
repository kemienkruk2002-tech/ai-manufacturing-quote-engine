using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ApiProblemDetailsTests(PostgresFixture db)
{
    [Fact]
    public async Task Empty_404_uses_problem_details_contract()
    {
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var response = await api.Client.GetAsync("/api/not-present");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("NOT_FOUND", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task Explicit_rfq_problem_keeps_specific_code_and_adds_correlation_id()
    {
        using var api = new RfqFileApiContext(db.ConnectionString, configurePolicy: false);
        using var response = await api.Client.PostAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{PostgresFixture.RequestId}/files/drawing",
            FileBody("content"u8.ToArray()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RFQ_FILE_POLICY_NOT_CONFIGURED", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task Domain_validation_exception_is_mapped_to_400()
    {
        using var factory = new ThrowingRfqFileApiFactory(db.ConnectionString,
            new DomainValidationException("RFQ_TEST_INVALID", "Controlled validation message."));
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, PostgresFixture.TenantId);
        using var response = await client.PostAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{PostgresFixture.RequestId}/files/drawing",
            FileBody("content"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DOMAIN_VALIDATION_FAILED", problem.GetProperty("code").GetString());
        Assert.Equal("RFQ_TEST_INVALID",
            problem.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task Missing_snapshot_input_exception_is_mapped_to_422()
    {
        using var factory = new ThrowingRfqFileApiFactory(db.ConnectionString,
            new MissingSnapshotInputException("PART_REVISION_MISSING", "Controlled missing input."));
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, PostgresFixture.TenantId);
        using var response = await client.PostAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{PostgresFixture.RequestId}/files/drawing",
            FileBody("content"u8.ToArray()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PART_REVISION_MISSING", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task Unexpected_exception_returns_safe_500_without_message_leak()
    {
        const string secretMarker = "SECRET_SQL_PASSWORD=must-not-leak";
        using var factory = new ThrowingRfqFileApiFactory(db.ConnectionString,
            new InvalidOperationException(secretMarker));
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, PostgresFixture.TenantId);
        using var response = await client.PostAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{PostgresFixture.RequestId}/files/drawing",
            FileBody("content"u8.ToArray()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretMarker, raw, StringComparison.Ordinal);
        var problem = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("INTERNAL_SERVER_ERROR", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task Unexpected_argument_exception_still_returns_sanitized_500()
    {
        const string secretMarker = "SECRET_INTERNAL_ARGUMENT=must-not-leak";
        using var factory = new ThrowingRfqFileApiFactory(db.ConnectionString,
            new ArgumentException(secretMarker));
        using var client = factory.CreateClient();
        TestAuthentication.Authenticate(client, PostgresFixture.TenantId);

        using var response = await client.PostAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{PostgresFixture.RequestId}/files/drawing",
            FileBody("content"u8.ToArray()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretMarker, raw, StringComparison.Ordinal);
        var problem = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("INTERNAL_SERVER_ERROR", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    private static MultipartFormDataContent FileBody(byte[] content)
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        body.Add(file, "file", "drawing.txt");
        return body;
    }
}

internal sealed class ThrowingRfqFileApiFactory(string connectionString, Exception exception)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuoteEngine"] = connectionString,
                ["Dev:GoldenEnabled"] = "false",
                ["RfqFiles:StorageRoot"] = Path.Combine(Path.GetTempPath(), "quoteengine-b1-1-tests"),
                ["RfqFiles:MaxUploadBytes"] = "1024",
                ["RfqFiles:AllowedMimeTypes:0"] = "text/plain"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            TestAuthentication.Configure(services);
            services.RemoveAll<IRfqFileRepository>();
            services.AddSingleton<IRfqFileRepository>(new ThrowingRfqFileRepository(exception));
        });
    }
}

internal sealed class ThrowingRfqFileRepository(Exception exception) : IRfqFileRepository
{
    public Task<bool> RequestExistsAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(exception);

    public Task<RfqFileVersion> GetOrCreateVersionAsync(RfqFileVersionInput input,
        CancellationToken cancellationToken = default) =>
        Task.FromException<RfqFileVersion>(new NotSupportedException());

    public Task<RfqFileVersion?> FindVersionAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, int versionNo, CancellationToken cancellationToken = default) =>
        Task.FromException<RfqFileVersion?>(new NotSupportedException());

    public Task<IReadOnlyList<RfqFileVersion>> ListVersionsAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<RfqFileVersion>>(new NotSupportedException());
}
