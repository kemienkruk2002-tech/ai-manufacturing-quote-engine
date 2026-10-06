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
                .Select(TimeSpan.FromMilliseconds)
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
        return services;
    }

    private static bool IsValidHttpsBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private sealed class DisabledAiStructuredProvider : IAiStructuredProvider
    {
        public Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiProviderResult.Failure(
                AiProviderFailureKind.PERMANENT, DisabledCode));
    }
}
