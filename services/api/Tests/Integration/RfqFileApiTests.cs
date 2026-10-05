using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqFileApiTests(PostgresFixture db)
{
    [Fact]
    public async Task Draft_without_part_or_quantity_accepts_upload()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var response = await api.Client.PostAsync(UploadUrl(PostgresFixture.TenantId, requestId, "drawing"),
            FileBody("draft"u8.ToArray(), "drawing.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionNo").GetInt32());
    }

    [Fact]
    public async Task Upload_then_download_returns_exact_v1_content_and_headers()
    {
        var content = "exact-v1"u8.ToArray();
        using var api = new RfqFileApiContext(db.ConnectionString);
        await UploadOkAsync(api.Client, PostgresFixture.RequestId, "exact", content, "customer drawing.txt", "text/plain");

        using var response = await api.Client.GetAsync(DownloadUrl(PostgresFixture.TenantId, PostgresFixture.RequestId, "exact", 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal("customer drawing.txt", fileName);
    }

    [Fact]
    public async Task Same_content_returns_same_version_and_one_metadata_row()
    {
        var requestId = await CreateDraftAsync();
        var content = "same"u8.ToArray();
        using var api = new RfqFileApiContext(db.ConnectionString);

        var first = await UploadOkAsync(api.Client, requestId, "same", content, "one.txt", "text/plain");
        var second = await UploadOkAsync(api.Client, requestId, "same", content, "two.txt", "text/plain");

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(1, second.GetProperty("versionNo").GetInt32());
        Assert.Equal(1L, await MetadataCountAsync(requestId, "same"));
    }

    [Fact]
    public async Task Different_content_creates_v2_without_changing_v1()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        var v1 = "version-one"u8.ToArray();
        var v2 = "version-two"u8.ToArray();
        await UploadOkAsync(api.Client, requestId, "versions", v1, "v1.txt", "text/plain");
        var created = await UploadOkAsync(api.Client, requestId, "versions", v2, "v2.txt", "text/plain");

        Assert.Equal(2, created.GetProperty("versionNo").GetInt32());
        Assert.Equal(v1, await api.Client.GetByteArrayAsync(DownloadUrl(PostgresFixture.TenantId, requestId, "versions", 1)));
        Assert.Equal(v2, await api.Client.GetByteArrayAsync(DownloadUrl(PostgresFixture.TenantId, requestId, "versions", 2)));
    }

    [Fact]
    public async Task Wrong_tenant_cannot_download_version()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        await UploadOkAsync(api.Client, requestId, "tenant", "secret"u8.ToArray(), "secret.txt", "text/plain");

        using var response = await api.Client.GetAsync(DownloadUrl(Guid.NewGuid(), requestId, "tenant", 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("TENANT_RESOURCE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_trusted_tenant_context_is_unauthorized()
    {
        using var api = new RfqFileApiContext(db.ConnectionString, authenticate: false);

        using var response = await api.Client.GetAsync(DownloadUrl(
            PostgresFixture.TenantId, PostgresFixture.RequestId, "missing-context", 1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("TENANT_CONTEXT_REQUIRED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Wrong_request_cannot_download_version()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        await UploadOkAsync(api.Client, requestId, "request", "secret"u8.ToArray(), "secret.txt", "text/plain");

        using var response = await api.Client.GetAsync(DownloadUrl(
            PostgresFixture.TenantId, Guid.NewGuid(), "request", 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_logical_key_cannot_download_version()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        await UploadOkAsync(api.Client, requestId, "correct-key", "secret"u8.ToArray(), "secret.txt", "text/plain");

        using var response = await api.Client.GetAsync(DownloadUrl(
            PostgresFixture.TenantId, requestId, "wrong-key", 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_version_number_cannot_download_version()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        await UploadOkAsync(api.Client, requestId, "version", "secret"u8.ToArray(), "secret.txt", "text/plain");

        using var response = await api.Client.GetAsync(DownloadUrl(
            PostgresFixture.TenantId, requestId, "version", 2));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversize_is_rejected_without_metadata_or_object()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString, maxUploadBytes: 4);
        using var response = await api.Client.PostAsync(UploadUrl(PostgresFixture.TenantId, requestId, "oversize"),
            FileBody("12345"u8.ToArray(), "large.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await MetadataCountAsync(requestId, "oversize"));
        Assert.Equal(0, api.StoredObjectCount);
    }

    [Fact]
    public async Task Blocked_mime_is_rejected_without_metadata_or_object()
    {
        var requestId = await CreateDraftAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var response = await api.Client.PostAsync(UploadUrl(PostgresFixture.TenantId, requestId, "mime"),
            FileBody("pdf"u8.ToArray(), "drawing.pdf", "application/pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0L, await MetadataCountAsync(requestId, "mime"));
        Assert.Equal(0, api.StoredObjectCount);
    }

    [Fact]
    public async Task Missing_policy_returns_explicit_service_unavailable_problem()
    {
        using var api = new RfqFileApiContext(db.ConnectionString, configurePolicy: false);
        using var response = await api.Client.PostAsync(UploadUrl(PostgresFixture.TenantId, PostgresFixture.RequestId, "policy"),
            FileBody("content"u8.ToArray(), "file.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("RFQ_FILE_POLICY_NOT_CONFIGURED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, api.StoredObjectCount);
    }

    [Fact]
    public async Task Metadata_without_bytes_returns_explicit_internal_error()
    {
        var requestId = await CreateDraftAsync();
        var content = "missing"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await new RfqFileRepository(db.DataSource).GetOrCreateVersionAsync(new(PostgresFixture.TenantId,
            requestId, "missing-object", "missing.txt", "text/plain", content.LongLength, hash));
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.GetAsync(DownloadUrl(PostgresFixture.TenantId, requestId, "missing-object", 1));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("RFQ_FILE_OBJECT_MISSING",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private async Task<Guid> CreateDraftAsync()
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status) VALUES(@id,@tenant,'New')
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<long> MetadataCountAsync(Guid requestId, string logicalKey)
    {
        await using var command = db.DataSource.CreateCommand("""
            SELECT count(*) FROM quote_request_document_versions v
            JOIN quote_request_documents d ON d.tenant_id=v.tenant_id AND d.id=v.document_id
            WHERE d.tenant_id=@tenant AND d.quote_request_id=@request AND d.logical_key=@key
            """);
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("key", logicalKey);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> UploadOkAsync(HttpClient client, Guid requestId, string logicalKey,
        byte[] content, string fileName, string mime)
    {
        using var response = await client.PostAsync(UploadUrl(PostgresFixture.TenantId, requestId, logicalKey),
            FileBody(content, fileName, mime));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent FileBody(byte[] content, string fileName, string mime)
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
        body.Add(file, "file", fileName);
        return body;
    }

    private static string UploadUrl(Guid tenantId, Guid requestId, string logicalKey) =>
        $"/api/tenants/{tenantId}/rfqs/{requestId}/files/{logicalKey}";

    private static string DownloadUrl(Guid tenantId, Guid requestId, string logicalKey, int version) =>
        $"{UploadUrl(tenantId, requestId, logicalKey)}/versions/{version}";
}

internal sealed class RfqFileApiContext : IDisposable
{
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), "quoteengine-b058-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> factory;
    public HttpClient Client { get; }
    public int StoredObjectCount => Directory.Exists(storageRoot)
        ? Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories).Count()
        : 0;

    public RfqFileApiContext(string connectionString, long maxUploadBytes = 1024, bool configurePolicy = true,
        bool authenticate = true)
    {
        factory = new RfqFileApiFactory(connectionString, storageRoot, maxUploadBytes, configurePolicy);
        Client = factory.CreateClient();
        if (authenticate)
            TestAuthentication.Authenticate(Client, PostgresFixture.TenantId);
    }

    public void Dispose()
    {
        Client.Dispose();
        factory.Dispose();
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var resolved = Path.GetFullPath(storageRoot);
        if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(resolved).StartsWith("quoteengine-b058-", StringComparison.Ordinal)
            && Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}

internal sealed class RfqFileApiFactory(string connectionString, string storageRoot, long maxUploadBytes,
    bool configurePolicy) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuoteEngine"] = connectionString,
                ["Dev:GoldenEnabled"] = "false"
            };
            if (configurePolicy)
            {
                values["RfqFiles:StorageRoot"] = storageRoot;
                values["RfqFiles:MaxUploadBytes"] = maxUploadBytes.ToString();
                values["RfqFiles:AllowedMimeTypes:0"] = "text/plain";
            }
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(TestAuthentication.Configure);
    }
}
