using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class AiExecutionPolicyV1Tests
{
    [Fact]
    public async Task Explicit_external_ai_flag_is_required_before_redaction_or_provider()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));
        var executor = Executor(provider, redactor);

        var result = await executor.ExecuteAsync(ExternalRequest(allowExternalAi: false));

        AssertReview(result, AiPolicyExecutorV1.ExternalAiNotAllowedCode);
        Assert.Equal(0, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData("other-use-case", "model-a", "test-document", AiPolicyExecutorV1.UseCaseNotPermittedCode)]
    [InlineData("rfq-extractor", "other-model", "test-document", AiPolicyExecutorV1.ModelNotPermittedCode)]
    [InlineData("rfq-extractor", "model-a", "other-document", AiPolicyExecutorV1.DocumentTypeNotPermittedCode)]
    public async Task Allowlist_mismatch_never_calls_redactor_or_provider(
        string useCase, string model, string documentType, string expectedCode)
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));
        var executor = Executor(provider, redactor);

        var result = await executor.ExecuteAsync(new(
            Request(useCase, model),
            true,
            [documentType]));

        AssertReview(result, expectedCode);
        Assert.Equal(0, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Document_type_is_required_for_external_execution()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));

        var result = await Executor(provider, redactor).ExecuteAsync(new(Request(), true, []));

        AssertReview(result, AiPolicyExecutorV1.DocumentTypeRequiredCode);
        Assert.Equal(0, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Missing_positive_payload_limit_fails_closed()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));
        var executor = Executor(provider, redactor, maxPayloadBytes: 0);

        var result = await executor.ExecuteAsync(ExternalRequest());

        AssertReview(result, AiPolicyExecutorV1.PayloadLimitNotConfiguredCode);
        Assert.Equal(0, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Oversized_input_is_blocked_before_redaction()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));
        var executor = Executor(provider, redactor, maxPayloadBytes: 5);

        var result = await executor.ExecuteAsync(ExternalRequest());

        AssertReview(result, AiPolicyExecutorV1.PayloadTooLargeCode);
        Assert.Equal(0, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Redaction_block_is_review_manual_without_provider_fallback()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Blocked("TEST_REDACTION_BLOCK"));

        var result = await Executor(provider, redactor).ExecuteAsync(ExternalRequest());

        AssertReview(result, "TEST_REDACTION_BLOCK");
        Assert.Equal(1, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Invalid_redactor_output_is_review_manual()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("{"));

        var result = await Executor(provider, redactor).ExecuteAsync(ExternalRequest());

        AssertReview(result, AiPolicyExecutorV1.RedactionInvalidCode);
        Assert.Equal(1, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Payload_limit_is_rechecked_after_redaction()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed(
            """{"expanded":"this-is-longer-than-the-limit"}"""));
        var executor = Executor(provider, redactor, maxPayloadBytes: 24);

        var result = await executor.ExecuteAsync(ExternalRequest());

        AssertReview(result, AiPolicyExecutorV1.PayloadTooLargeCode);
        Assert.Equal(1, redactor.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Permitted_redacted_input_is_the_only_input_seen_by_gateway_provider()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed(
            """ { "safe": true, "part": "REDACTED" } """));
        var executor = Executor(provider, redactor);

        var result = await executor.ExecuteAsync(ExternalRequest());

        Assert.True(result.IsCompleted);
        Assert.Equal(AiExecutionDisposition.COMPLETED, result.Disposition);
        Assert.Null(result.Code);
        Assert.NotNull(result.GatewayResult);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("""{"part":"REDACTED","safe":true}""",
            provider.LastRequest!.NormalizedInputJson);

        var expectedFingerprint = AiRequestFingerprintV1.Create(
            Request() with { NormalizedInputJson = """{"part":"REDACTED","safe":true}""" });
        Assert.Equal(expectedFingerprint.Fingerprint, result.GatewayResult!.Fingerprint);
    }

    [Fact]
    public async Task Provider_failure_requires_review_manual_and_never_invents_fallback()
    {
        var provider = new CapturingProvider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "provider-down"));
        var redactor = new FakeRedactor(AiRedactionResult.Allowed("""{"part":"P-1"}"""));

        var result = await Executor(provider, redactor).ExecuteAsync(ExternalRequest());

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(AiGatewayV1.ProviderFailureCode, result.Code);
        Assert.NotNull(result.GatewayResult);
        Assert.Equal(AiProviderFailureKind.TRANSIENT, result.GatewayResult!.ProviderFailureKind);
        Assert.Equal("provider-down", result.GatewayResult.ProviderFailureCode);
        Assert.Equal(1, provider.CallCount);
    }

    private static AiPolicyExecutorV1 Executor(
        CapturingProvider provider,
        FakeRedactor redactor,
        int maxPayloadBytes = 4096) =>
        new(
            new AiGatewayV1(provider),
            redactor,
            new(
                new HashSet<string>(new[] { "rfq-extractor" }, StringComparer.Ordinal),
                new HashSet<string>(new[] { "model-a" }, StringComparer.Ordinal),
                new HashSet<string>(new[] { "test-document" }, StringComparer.Ordinal),
                maxPayloadBytes));

    private static AiExternalExecutionRequest ExternalRequest(bool allowExternalAi = true) =>
        new(Request(), allowExternalAi, ["test-document"]);

    private static AiStructuredRequest Request(
        string useCase = "rfq-extractor",
        string model = "model-a") =>
        new(useCase, model, "prompt-v1", "v1", """{"part":"P-1"}""");

    private static string ValidJson() => JsonSerializer.Serialize(ValidRfq());

    private static CanonicalRfqV1 ValidRfq() => new(
        TextFact("RFQ-POLICY"), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
        [], [], [], [], []);

    private static CanonicalRfqFact<string> TextFact(string value) => new([value], value,
        [new("rfq_file", "test-source")], 0.99m, RfqFactClassification.EXPLICIT, false, []);

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null,
        RfqFactClassification.MISSING, false, []);

    private static void AssertReview(AiPolicyExecutionResult result, string code)
    {
        Assert.False(result.IsCompleted);
        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(code, result.Code);
        Assert.Null(result.GatewayResult);
    }

    private sealed class FakeRedactor(AiRedactionResult result) : IAiInputRedactor
    {
        public int CallCount { get; private set; }

        public AiRedactionResult Redact(string normalizedInputJson)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class CapturingProvider(AiProviderResult result) : IAiStructuredProvider
    {
        public int CallCount { get; private set; }
        public AiStructuredRequest? LastRequest { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(
            AiStructuredRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
