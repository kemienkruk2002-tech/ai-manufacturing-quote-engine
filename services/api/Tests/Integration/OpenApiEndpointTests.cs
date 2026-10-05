using System.Net;
using System.Text.Json;

namespace QuoteEngine.IntegrationTests;

public sealed class OpenApiEndpointTests
{
    private const string OfflineConnection =
        "Host=127.0.0.1;Port=1;Database=openapi_unused;Username=openapi_unused;Password=openapi_unused;Timeout=1";

    [Fact]
    public async Task Production_schema_is_generated_without_database_connection()
    {
        await using var factory = new ApiFactory(OfflineConnection, "Production", false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/vnd.oai.openapi+json",
            response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("3.0.3", root.GetProperty("openapi").GetString());

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out var health));
        Assert.Equal("GetHealth", health.GetProperty("get").GetProperty("operationId").GetString());

        Assert.True(paths.TryGetProperty(
            "/api/tenants/{tenantId}/rfqs/{quoteRequestId}/files/{logicalKey}", out var upload));
        var uploadOperation = upload.GetProperty("post");
        Assert.Equal("UploadRfqFile", uploadOperation.GetProperty("operationId").GetString());
        Assert.True(uploadOperation.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("multipart/form-data", out _));

        Assert.True(paths.TryGetProperty(
            "/api/tenants/{tenantId}/rfqs/{quoteRequestId}/files/{logicalKey}/versions/{versionNo}",
            out var download));
        Assert.Equal("DownloadRfqFileVersion",
            download.GetProperty("get").GetProperty("operationId").GetString());

        Assert.False(paths.TryGetProperty("/api/dev/golden/w07044", out _));
        Assert.False(paths.TryGetProperty("/openapi/v1.json", out _));
    }

    [Fact]
    public async Task Development_schema_excludes_golden_endpoint()
    {
        await using var factory = new ApiFactory(OfflineConnection, "Development", true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("paths")
            .TryGetProperty("/api/dev/golden/w07044", out _));
    }
}
