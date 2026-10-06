using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuoteEngine.Api;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;
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
    public async Task Execution_policy_is_default_deny_with_no_allowlists_or_payload_limit()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var executor = factory.Services.GetRequiredService<AiPolicyExecutorV1>();
        var result = await executor.ExecuteAsync(new(
            new("rfq-extractor", "model-test", "prompt-v1", "v1", "{}"),
            true,
            ["test-document"]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(AiPolicyExecutorV1.UseCaseNotPermittedCode, result.Code);
        Assert.Null(result.GatewayResult);
    }

    [Fact]
    public async Task Fully_permitted_policy_still_blocks_until_redactor_is_explicitly_replaced()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "integration-test-secret",
            ["Ai:BaseUrl"] = "https://api.openai.com/",
            ["Ai:Policy:PermittedUseCases:0"] = "rfq-extractor",
            ["Ai:Policy:PermittedModels:0"] = "model-test",
            ["Ai:Policy:PermittedDocumentTypes:0"] = "test-document",
            ["Ai:Policy:MaxPayloadBytes"] = "4096"
        });
        using var client = factory.CreateClient();

        var executor = factory.Services.GetRequiredService<AiPolicyExecutorV1>();
        var result = await executor.ExecuteAsync(new(
            new("rfq-extractor", "model-test", "prompt-v1", "v1", "{}"),
            true,
            ["test-document"]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(AiPolicyExecutorV1.RedactionNotConfiguredCode, result.Code);
        Assert.Null(result.GatewayResult);
    }

    [Fact]
    public async Task Rfq_extraction_service_is_registered_and_default_materializer_fails_closed()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        var files = scope.ServiceProvider.GetRequiredService<IRfqFileRepository>();
        var logicalKey = "b34-" + Guid.NewGuid().ToString("N");
        await files.GetOrCreateVersionAsync(new(
            PostgresFixture.TenantId,
            PostgresFixture.RequestId,
            logicalKey,
            "rfq.txt",
            "text/plain",
            3,
            new string('a', 64),
            "test:b3.4"));

        var service = scope.ServiceProvider.GetRequiredService<RfqExtractionServiceV1>();
        var result = await service.ExecuteAsync(new(
            PostgresFixture.TenantId,
            PostgresFixture.RequestId,
            "model-test",
            true,
            [new(logicalKey, 1, "test-document")]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(RfqExtractionServiceV1.SourceMaterializerNotConfiguredCode, result.Code);
        Assert.Null(result.RequestFingerprint);
        Assert.NotNull(result.StoredExecution);
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
    public void Blank_policy_allowlist_value_fails_startup_validation()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Policy:PermittedModels:0"] = " "
        });

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.CreateClient();
        });

        Assert.Contains("Ai:Policy:PermittedModels cannot contain blank values.",
            error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_policy_payload_limit_fails_startup_validation()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Ai:Policy:MaxPayloadBytes"] = "-1"
        });

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.CreateClient();
        });

        Assert.Contains("Ai:Policy:MaxPayloadBytes must be greater than or equal to zero.",
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
