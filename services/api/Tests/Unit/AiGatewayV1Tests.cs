using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class AiGatewayV1Tests
{
    [Fact]
    public async Task Invalid_request_does_not_call_provider()
    {
        var provider = Provider(AiProviderResult.Success(ValidJson()));
        var result = await new AiGatewayV1(provider).ExecuteAsync(Request() with { ModelId = " " });

        AssertBlocked(result, "AI_REQUEST_INVALID");
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Unsupported_use_case_does_not_call_provider()
    {
        var provider = Provider(AiProviderResult.Success(ValidJson()));
        var result = await new AiGatewayV1(provider).ExecuteAsync(Request() with { UseCase = "other" });

        AssertBlocked(result, "AI_USE_CASE_NOT_SUPPORTED");
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Valid_request_and_output_call_provider_once_and_pass()
    {
        var provider = Provider(AiProviderResult.Success(ValidJson()));
        var result = await new AiGatewayV1(provider).ExecuteAsync(Request());

        Assert.True(result.IsPass);
        Assert.Equal(1, provider.CallCount);
        Assert.IsType<CanonicalRfqV1>(result.Output);
        Assert.Matches("^[0-9a-f]{64}$", result.Fingerprint!);
    }

    [Fact]
    public async Task Provider_failure_is_blocked_with_stable_code_and_kind()
    {
        var provider = Provider(AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "provider-unavailable"));
        var result = await new AiGatewayV1(provider).ExecuteAsync(Request());

        AssertBlocked(result, "AI_PROVIDER_FAILURE");
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(AiProviderFailureKind.TRANSIENT, result.ProviderFailureKind);
        Assert.Equal("provider-unavailable", result.ProviderFailureCode);
    }

    [Theory]
    [InlineData("malformed", "AI_OUTPUT_JSON_INVALID")]
    [InlineData("contract-invalid", "AI_OUTPUT_CONTRACT_INVALID")]
    public async Task Invalid_provider_output_preserves_guard_code(string outputKind, string expectedCode)
    {
        var rawJson = outputKind == "malformed"
            ? "{"
            : JsonSerializer.Serialize(ValidRfq() with
            {
                RfqNumber = TextFact("RFQ-64") with { SourceReferences = [] }
            });
        var provider = Provider(AiProviderResult.Success(rawJson));

        var result = await new AiGatewayV1(provider).ExecuteAsync(Request());

        AssertBlocked(result, expectedCode);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Same_request_one_hundred_times_has_same_fingerprint_and_status()
    {
        var provider = Provider(AiProviderResult.Success(ValidJson()));
        var gateway = new AiGatewayV1(provider);
        var results = new List<AiGatewayResult>();
        for (var index = 0; index < 100; index++) results.Add(await gateway.ExecuteAsync(Request()));

        Assert.Single(results.Select(result => result.Fingerprint).Distinct());
        Assert.Single(results.Select(result => result.Status).Distinct());
        Assert.All(results, result => Assert.True(result.IsPass));
        Assert.Equal(100, provider.CallCount);
    }

    private static FakeProvider Provider(AiProviderResult result) => new(result);

    private static AiStructuredRequest Request() => new("rfq-extractor", "model-a", "prompt-v1", "v1",
        "{\"part\":\"P-1\"}");

    private static string ValidJson() => JsonSerializer.Serialize(ValidRfq());

    private static CanonicalRfqV1 ValidRfq() => new(
        TextFact("RFQ-64"), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
        [], [], [], [], []);

    private static CanonicalRfqFact<string> TextFact(string value) => new([value], value,
        [new("rfq_file", "opaque-source-id")], 0.99m, RfqFactClassification.EXPLICIT, false, []);

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null,
        RfqFactClassification.MISSING, false, []);

    private static void AssertBlocked(AiGatewayResult result, string code)
    {
        Assert.Equal(AiOutputGuardStatus.BLOCKED, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Output);
    }

    private sealed class FakeProvider(AiProviderResult result) : IAiStructuredProvider
    {
        public int CallCount { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
