using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class AiStructuredBoundaryTests
{
    [Fact]
    public void Equivalent_object_json_normalizes_to_identical_bytes_and_fingerprint()
    {
        const string first = """
            {
              "b": 2,
              "a": {
                "z": true,
                "y": [ { "d": 4, "c": 3 }, 2 ]
              }
            }
            """;
        const string second = """{"a":{"y":[{"c":3,"d":4},2],"z":true},"b":2}""";

        var firstNormalized = AiInputJsonNormalizerV1.Normalize(first);
        var secondNormalized = AiInputJsonNormalizerV1.Normalize(second);

        Assert.True(firstNormalized.IsValid);
        Assert.True(secondNormalized.IsValid);
        Assert.Equal("""{"a":{"y":[{"c":3,"d":4},2],"z":true},"b":2}""",
            firstNormalized.NormalizedJson);
        Assert.Equal(firstNormalized.NormalizedJson, secondNormalized.NormalizedJson);
        Assert.Equal(
            Fingerprint(Request() with { NormalizedInputJson = first }),
            Fingerprint(Request() with { NormalizedInputJson = second }));
    }

    [Theory]
    [InlineData("""{"n":1}""")]
    [InlineData("""{"n":1.0}""")]
    [InlineData("""{"n":10e-1}""")]
    [InlineData("""{"n":0.10e1}""")]
    public void Equivalent_number_lexemes_have_identical_normalization_and_fingerprint(string json)
    {
        var normalized = AiInputJsonNormalizerV1.Normalize(json);

        Assert.True(normalized.IsValid);
        Assert.Equal("""{"n":1}""", normalized.NormalizedJson);
        Assert.Equal(
            Fingerprint(Request() with { NormalizedInputJson = """{"n":1}""" }),
            Fingerprint(Request() with { NormalizedInputJson = json }));
    }

    [Theory]
    [InlineData("""{"n":1000}""", """{"n":1e3}""")]
    [InlineData("""{"n":0.001}""", """{"n":1e-3}""")]
    [InlineData("""{"n":-0.0}""", """{"n":0}""")]
    public void Exact_decimal_number_normalization_does_not_require_floating_point(
        string input, string expected)
    {
        var normalized = AiInputJsonNormalizerV1.Normalize(input);

        Assert.True(normalized.IsValid);
        Assert.Equal(expected, normalized.NormalizedJson);
    }

    [Fact]
    public void Array_order_and_value_types_remain_semantically_significant()
    {
        var baseline = Fingerprint(Request() with { NormalizedInputJson = """{"a":[1,"1"]}""" });
        var reversed = Fingerprint(Request() with { NormalizedInputJson = """{"a":["1",1]}""" });
        var changedType = Fingerprint(Request() with { NormalizedInputJson = """{"a":[1,1]}""" });

        Assert.NotEqual(baseline, reversed);
        Assert.NotEqual(baseline, changedType);
        Assert.NotEqual(reversed, changedType);
    }

    [Fact]
    public void Duplicate_property_names_are_rejected_as_ambiguous_input()
    {
        var normalization = AiInputJsonNormalizerV1.Normalize("""{"a":1,"a":1}""");
        var fingerprint = AiRequestFingerprintV1.Create(
            Request() with { NormalizedInputJson = """{"a":1,"a":1}""" });

        Assert.False(normalization.IsValid);
        Assert.Null(normalization.NormalizedJson);
        Assert.False(fingerprint.IsValid);
        Assert.Equal("AI_REQUEST_INVALID", fingerprint.Code);
        Assert.Null(fingerprint.Fingerprint);
    }

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
