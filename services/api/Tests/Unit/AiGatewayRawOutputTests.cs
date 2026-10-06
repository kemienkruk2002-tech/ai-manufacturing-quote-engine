using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class AiGatewayRawOutputTests
{
    [Fact]
    public async Task Validated_provider_output_is_retained_verbatim_for_history_persistence()
    {
        var raw = JsonSerializer.Serialize(new CanonicalRfqV1(
            Missing<string>(), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
            [], [], [], [], []));
        var gateway = new AiGatewayV1(new Provider(raw));
        var result = await gateway.ExecuteAsync(new(RfqExtractorPromptV1.UseCase, "model-a",
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion, "{}"));
        Assert.True(result.IsPass);
        Assert.Equal(raw, result.RawProviderJson);
    }

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null,
        RfqFactClassification.MISSING, false, []);

    private sealed class Provider(string raw) : IAiStructuredProvider
    {
        public Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(AiProviderResult.Success(raw));
    }
}
