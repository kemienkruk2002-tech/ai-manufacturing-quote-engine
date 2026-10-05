using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DevelopmentEndpointTests(PostgresFixture db)
{
    [Fact]
    public async Task Development_endpoint_returns_golden_trace()
    {
        await using var factory = new ApiFactory(db.ConnectionString, "Development", true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/dev/golden/w07044");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("W07044", root.GetProperty("part").GetProperty("partNumber").GetString());
        Assert.Equal(8, root.GetProperty("route").GetProperty("operations").GetArrayLength());
        Assert.Equal(61.125m, root.GetProperty("calculatedTimeResult").GetProperty("laborHoursBatch").GetDecimal());
        Assert.Equal(64, root.GetProperty("snapshot_hash").GetString()!.Length);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task Golden_endpoint_is_not_public(string environment, bool enabled)
    {
        await using var factory = new ApiFactory(db.ConnectionString, environment, enabled);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/dev/golden/w07044");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

internal sealed class ApiFactory(string connectionString, string environment, bool goldenEnabled) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QuoteEngine"] = connectionString,
            ["Dev:GoldenEnabled"] = goldenEnabled.ToString()
        }));
    }
}
