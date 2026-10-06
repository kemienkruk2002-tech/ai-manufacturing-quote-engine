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
    public static IServiceCollection AddQuoteEngineAi(this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(AiIntegrationOptions.SectionName);
        services.AddOptions<AiIntegrationOptions>()
            .Bind(section)
            .Validate(options => !options.Enabled || IsValidHttpsBaseUrl(options.BaseUrl),
                "Ai:BaseUrl must be an absolute HTTPS URL when AI is enabled.")
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Ai:ApiKey is required when AI is enabled.")
            .Validate(options => options.RetryDelaysMs.All(delay => delay >= 0),
                "Ai:RetryDelaysMs values must be greater than or equal to zero.")
            .ValidateOnStart();

        if (!section.GetValue<bool>(nameof(AiIntegrationOptions.Enabled)))
            return services;

        services.AddHttpClient(AiHttpClientNames.OpenAiResponses, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AiIntegrationOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });

        services.AddTransient<OpenAiResponsesProviderV1>(provider =>
            new OpenAiResponsesProviderV1(
                provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(AiHttpClientNames.OpenAiResponses)));

        services.AddSingleton<IAiRetryDelay, SystemAiRetryDelay>();
        services.AddTransient<IAiStructuredProvider>(provider =>
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
        services.AddTransient<AiGatewayV1>();
        return services;
    }

    private static bool IsValidHttpsBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}
