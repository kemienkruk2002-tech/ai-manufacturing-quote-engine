using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuoteEngine.Api;
using QuoteEngine.Application.Ai;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AiServiceRegistrationTests(PostgresFixture db)
{
    [Fact]
    public async Task Ai_is_disabled_by_default_and_gateway_fails_closed_without_network()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.Enabled);
        Assert.NotNull(factory.Services.GetRequiredService<AiGatewayV1>());
        Assert.NotNull(factory.Services.GetRequiredService<OpenAiResponsesProviderV1>());
        Assert.NotNull(factory.Services.GetRequiredService<RetryingAiStructuredProviderV1>());

        var result = await factory.Services.GetRequiredService<AiGatewayV1>().ExecuteAsync(
            new("rfq-extractor", "model-test", "prompt-v1", "v1", "{}"));

        Assert.Equal(AiOutputGuardStatus.BLOCKED, result.Status);
        Assert.Equal(AiGatewayV1.ProviderFailureCode, result.Code);
        Assert.Equal(AiProviderFailureKind.PERMANENT, result.ProviderFailureKind);
        Assert.Equal(AiServiceRegistration.DisabledCode, result.ProviderFailureCode);
    }

    [Fact]
    public void Enabled_ai_registers_retry_provider_and_configured_http_client()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "integration-test-secret",
            ["Ai:BaseUrl"] = "https://api.openai.com/",
            ["Ai:RetryDelaysMs:0"] = "25",
            ["Ai:RetryDelaysMs:1"] = "100"
        });

        using var client = factory.CreateClient();

        Assert.NotNull(factory.Services.GetRequiredService<AiGatewayV1>());
        Assert.IsType<RetryingAiStructuredProviderV1>(
            factory.Services.GetRequiredService<IAiStructuredProvider>());
        Assert.NotNull(factory.Services.GetRequiredService<OpenAiResponsesProviderV1>());

        var httpClient = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AiHttpClientNames.OpenAiResponses);
        Assert.Equal(new Uri("https://api.openai.com/"), httpClient.BaseAddress);
        Assert.Equal("Bearer", httpClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("integration-test-secret", httpClient.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Enabled_ai_without_api_key_fails_startup_validation()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:BaseUrl"] = "https://api.openai.com/"
        });

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.CreateClient();
        });

        Assert.Contains("Ai:ApiKey is required when AI is enabled.", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_ai_with_non_https_base_url_fails_startup_validation()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "integration-test-secret",
            ["Ai:BaseUrl"] = "http://api.openai.invalid/"
        });

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.CreateClient();
        });

        Assert.Contains("Ai:BaseUrl must be an absolute HTTPS URL when AI is enabled.", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_ai_with_negative_retry_delay_fails_startup_validation()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "integration-test-secret",
            ["Ai:RetryDelaysMs:0"] = "-1"
        });

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.CreateClient();
        });

        Assert.Contains("Ai:RetryDelaysMs values must be greater than or equal to zero when AI is enabled.",
            error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_ai_does_not_require_api_key_or_retry_policy()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "false",
            ["Ai:BaseUrl"] = "not-a-url",
            ["Ai:RetryDelaysMs:0"] = "-1"
        });

        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.Enabled);
    }

    private AiRegistrationFactory Factory(IReadOnlyDictionary<string, string?>? ai = null) =>
        new(db.ConnectionString, ai ?? new Dictionary<string, string?>());
}

internal sealed class AiRegistrationFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?> aiConfiguration)
    : WebApplicationFactory<Program>
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
            foreach (var pair in aiConfiguration)
                values[pair.Key] = pair.Value;
            configuration.AddInMemoryCollection(values);
        });
    }
}
