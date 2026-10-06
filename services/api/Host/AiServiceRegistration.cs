using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using QuoteEngine.Application.Ai;

namespace QuoteEngine.Api;

public sealed class AiIntegrationOptions
{
    public const string SectionName = "Ai";
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "https://api.openai.com/";
    public string? ApiKey { get; init; }
    public int[] RetryDelaysMs { get; init; } = [];
}

public sealed class AiExecutionPolicyOptions
{
    public const string SectionName = "Ai:Policy";
    public string[] PermittedUseCases { get; init; } = [];
    public string[] PermittedModels { get; init; } = [];
    public string[] PermittedDocumentTypes { get; init; } = [];
    public int MaxPayloadBytes { get; init; }
}

public static class AiHttpClientNames
{
    public const string OpenAiResponses = "QuoteEngine.OpenAI.Responses";
}

public static class AiServiceRegistration
{
    public const string DisabledCode = "AI_DISABLED";
    private static readonly Uri DefaultOpenAiBaseUri = new("https://api.openai.com/");

    public static IServiceCollection AddQuoteEngineAi(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiIntegrationOptions>()
            .Bind(configuration.GetSection(AiIntegrationOptions.SectionName))
            .Validate(options => !options.Enabled || IsValidHttpsBaseUrl(options.BaseUrl),
                "Ai:BaseUrl must be an absolute HTTPS URL when AI is enabled.")
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Ai:ApiKey is required when AI is enabled.")
            .Validate(options => !options.Enabled || options.RetryDelaysMs.All(delay => delay >= 0),
                "Ai:RetryDelaysMs values must be greater than or equal to zero when AI is enabled.")
            .ValidateOnStart();

        services.AddOptions<AiExecutionPolicyOptions>()
            .Bind(configuration.GetSection(AiExecutionPolicyOptions.SectionName))
            .Validate(options => options.MaxPayloadBytes >= 0,
                "Ai:Policy:MaxPayloadBytes must be greater than or equal to zero.")
            .Validate(options => ValidEntries(options.PermittedUseCases),
                "Ai:Policy:PermittedUseCases cannot contain blank values.")
            .Validate(options => ValidEntries(options.PermittedModels),
                "Ai:Policy:PermittedModels cannot contain blank values.")
            .Validate(options => ValidEntries(options.PermittedDocumentTypes),
                "Ai:Policy:PermittedDocumentTypes cannot contain blank values.")
            .ValidateOnStart();

        services.AddHttpClient(AiHttpClientNames.OpenAiResponses, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AiIntegrationOptions>>().Value;
            client.BaseAddress = options.Enabled
                ? new Uri(options.BaseUrl, UriKind.Absolute)
                : DefaultOpenAiBaseUri;
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });

        services.AddTransient<OpenAiResponsesProviderV1>(provider =>
            new OpenAiResponsesProviderV1(
                provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(AiHttpClientNames.OpenAiResponses)));

        services.AddSingleton<IAiRetryDelay, SystemAiRetryDelay>();
        services.AddTransient<RetryingAiStructuredProviderV1>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AiIntegrationOptions>>().Value;
            var delays = options.RetryDelaysMs
                .Select(delay => TimeSpan.FromMilliseconds(delay))
                .ToArray();
            return new RetryingAiStructuredProviderV1(
                provider.GetRequiredService<OpenAiResponsesProviderV1>(),
                delays,
                provider.GetRequiredService<IAiRetryDelay>());
        });
        services.AddTransient<IAiStructuredProvider>(provider =>
            provider.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.Enabled
                ? provider.GetRequiredService<RetryingAiStructuredProviderV1>()
                : new DisabledAiStructuredProvider());
        services.AddTransient<AiGatewayV1>();
        services.AddSingleton<IAiInputRedactor, BlockingAiInputRedactor>();
        services.AddSingleton<IRfqExtractionSourceMaterializer, BlockingRfqExtractionSourceMaterializer>();
        services.AddTransient<AiExecutionPolicyV1>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AiExecutionPolicyOptions>>().Value;
            return new(
                options.PermittedUseCases.ToHashSet(StringComparer.Ordinal),
                options.PermittedModels.ToHashSet(StringComparer.Ordinal),
                options.PermittedDocumentTypes.ToHashSet(StringComparer.Ordinal),
                options.MaxPayloadBytes);
        });
        services.AddTransient<AiPolicyExecutorV1>();
        services.AddScoped<RfqExtractionServiceV1>();
        return services;
    }

    private static bool IsValidHttpsBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static bool ValidEntries(IEnumerable<string> values) =>
        values.All(value => !string.IsNullOrWhiteSpace(value));

    private sealed class BlockingRfqExtractionSourceMaterializer
        : IRfqExtractionSourceMaterializer
    {
        public Task<RfqExtractionSourceMaterializationResult> MaterializeAsync(
            QuoteEngine.Domain.Quoting.RfqFileVersion source,
            string documentType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RfqExtractionSourceMaterializationResult.Failure(
                RfqExtractionServiceV1.SourceMaterializerNotConfiguredCode));
    }

    private sealed class BlockingAiInputRedactor : IAiInputRedactor
    {
        public AiRedactionResult Redact(string normalizedInputJson) =>
            AiRedactionResult.Blocked(AiPolicyExecutorV1.RedactionNotConfiguredCode);
    }

    private sealed class DisabledAiStructuredProvider : IAiStructuredProvider
    {
        public Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiProviderResult.Failure(
                AiProviderFailureKind.PERMANENT, DisabledCode));
    }
}
