using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class AiStructuredBoundaryTests
{
    [Fact]
    public void Same_request_one_hundred_times_has_identical_lowercase_sha256()
    {
        var request = Request();
        var hashes = Enumerable.Range(0, 100)
            .Select(_ => AiRequestFingerprintV1.Create(request).Fingerprint)
            .ToArray();

        Assert.Single(hashes.Distinct());
        Assert.Matches("^[0-9a-f]{64}$", hashes[0]!);
    }

    [Fact]
    public void Changing_each_request_component_changes_the_fingerprint()
    {
        var request = Request();
        var original = Fingerprint(request);
        var changed = new[]
        {
            Fingerprint(request with { UseCase = "other-use-case" }),
            Fingerprint(request with { ModelId = "other-model" }),
            Fingerprint(request with { PromptVersion = "prompt-v2" }),
            Fingerprint(request with { SchemaVersion = "schema-v2" }),
            Fingerprint(request with { NormalizedInputJson = "{\"part\":\"P-2\"}" })
        };

        Assert.All(changed, hash => Assert.NotEqual(original, hash));
        Assert.Equal(changed.Length, changed.Distinct().Count());
    }

    [Theory]
    [InlineData("use_case")]
    [InlineData("model_id")]
    [InlineData("prompt_version")]
    [InlineData("schema_version")]
    [InlineData("normalized_input_json")]
    public void Blank_required_field_has_stable_validation_code(string field)
    {
        var request = field switch
        {
            "use_case" => Request() with { UseCase = " " },
            "model_id" => Request() with { ModelId = " " },
            "prompt_version" => Request() with { PromptVersion = " " },
            "schema_version" => Request() with { SchemaVersion = " " },
            _ => Request() with { NormalizedInputJson = " " }
        };

        var result = AiRequestFingerprintV1.Create(request);
        Assert.False(result.IsValid);
        Assert.Equal("AI_REQUEST_INVALID", result.Code);
        Assert.Null(result.Fingerprint);
    }

    [Fact]
    public void Invalid_normalized_input_json_has_stable_validation_code()
    {
        var result = AiRequestFingerprintV1.Create(Request() with { NormalizedInputJson = "{" });

        Assert.False(result.IsValid);
        Assert.Equal("AI_REQUEST_INVALID", result.Code);
    }

    [Fact]
    public void Valid_rfq_extractor_v1_output_passes_and_returns_contract()
    {
        var result = RfqExtractorOutputGuardV1.Evaluate(ValidJson());

        Assert.True(result.IsPass);
        Assert.Null(result.Code);
        Assert.IsType<CanonicalRfqV1>(result.Output);
    }

    [Theory]
    [InlineData("RFQ_EXTRACTOR_V2", "v1")]
    [InlineData("RFQ_EXTRACTOR", "v2")]
    public void Wrong_schema_or_version_is_blocked(string schema, string version)
    {
        var json = ValidJson()
            .Replace("\"RFQ_EXTRACTOR\"", $"\"{schema}\"", StringComparison.Ordinal)
            .Replace("\"v1\"", $"\"{version}\"", StringComparison.Ordinal);

        AssertBlocked(json, "AI_OUTPUT_SCHEMA_MISMATCH");
    }

    [Fact]
    public void Malformed_json_is_blocked_with_json_invalid()
    {
        AssertBlocked("{", "AI_OUTPUT_JSON_INVALID");
    }

    [Fact]
    public void Explicit_without_source_is_blocked_with_contract_invalid()
    {
        var invalid = ValidRfq() with
        {
            RfqNumber = TextFact("RFQ-63") with { SourceReferences = [] }
        };

        AssertBlocked(JsonSerializer.Serialize(invalid), "AI_OUTPUT_CONTRACT_INVALID");
    }

    [Fact]
    public void Extra_top_level_property_is_blocked()
    {
        var json = ValidJson();
        var withExtra = json[..^1] + ",\"extra\":true}";

        AssertBlocked(withExtra, "AI_OUTPUT_CONTRACT_INVALID");
    }

    private static string Fingerprint(AiStructuredRequest request) =>
        AiRequestFingerprintV1.Create(request).Fingerprint!;

    private static AiStructuredRequest Request() => new("rfq-extractor", "model-a", "prompt-v1", "v1",
        "{\"part\":\"P-1\"}");

    private static string ValidJson() => JsonSerializer.Serialize(ValidRfq());

    private static CanonicalRfqV1 ValidRfq() => new(
        TextFact("RFQ-63"), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
        [], [], [], [], []);

    private static CanonicalRfqFact<string> TextFact(string value) => new([value], value,
        [new("rfq_file", "opaque-source-id")], 0.99m, RfqFactClassification.EXPLICIT, false, []);

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null,
        RfqFactClassification.MISSING, false, []);

    private static void AssertBlocked(string json, string code)
    {
        var result = RfqExtractorOutputGuardV1.Evaluate(json);
        Assert.Equal(AiOutputGuardStatus.BLOCKED, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Output);
    }
}
