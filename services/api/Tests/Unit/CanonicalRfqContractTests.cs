using System.Text.Json;
using System.Globalization;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class CanonicalRfqContractTests
{
    [Fact]
    public void Explicit_fact_with_source_is_valid()
    {
        var result = Validate(BaseRfq() with { RfqNumber = TextFact("RFQ-62") });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Explicit_fact_without_source_is_blocked()
    {
        var fact = TextFact("RFQ-62") with { SourceReferences = [] };

        AssertCode(Validate(BaseRfq() with { RfqNumber = fact }), "RFQ_EXPLICIT_SOURCE_REQUIRED");
    }

    [Fact]
    public void Inferred_fact_without_confirmation_is_blocked()
    {
        var fact = TextFact("REV-A") with
        {
            Classification = RfqFactClassification.INFERRED,
            RequiresConfirmation = false
        };

        AssertCode(Validate(BaseRfq() with { Revisions = [fact] }), "RFQ_INFERRED_CONFIRMATION_REQUIRED");
    }

    [Fact]
    public void Missing_fact_keeps_null_and_creates_no_default()
    {
        var missingQuantity = Missing<int?>();
        var rfq = BaseRfq() with { Quantities = [missingQuantity] };

        Assert.True(Validate(rfq).IsValid);
        Assert.Null(rfq.Quantities[0].NormalizedValue);
        Assert.Empty(rfq.Revisions);
        Assert.Empty(rfq.MaterialMentions);
    }

    [Fact]
    public void Conflict_with_two_values_and_sources_is_valid()
    {
        var fact = new CanonicalRfqFact<string>(["REV-A", "REV-B"], "REV-A",
            [Source("mail", "message-1"), Source("pdf", "document-2")], 0.55m,
            RfqFactClassification.CONFLICT, true, ["Conflicting revisions"]);

        Assert.True(Validate(BaseRfq() with { Revisions = [fact] }).IsValid);
    }

    [Fact]
    public void Conflict_with_one_value_is_blocked()
    {
        var fact = new CanonicalRfqFact<string>(["REV-A"], "REV-A",
            [Source("mail", "message-1"), Source("pdf", "document-2")], 0.55m,
            RfqFactClassification.CONFLICT, true, []);

        AssertCode(Validate(BaseRfq() with { Revisions = [fact] }), "RFQ_CONFLICT_RAW_VALUES_REQUIRED");
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    public void Confidence_outside_zero_to_one_is_blocked(string confidence)
    {
        var fact = TextFact("RFQ-62") with
        {
            ModelConfidence = decimal.Parse(confidence, CultureInfo.InvariantCulture)
        };

        AssertCode(Validate(BaseRfq() with { RfqNumber = fact }), "RFQ_FACT_CONFIDENCE_OUT_OF_RANGE");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_quantity_is_blocked(int quantity)
    {
        AssertCode(Validate(BaseRfq() with { Quantities = [QuantityFact(quantity)] }),
            "RFQ_QUANTITY_MUST_BE_POSITIVE");
    }

    [Fact]
    public void Positive_quantity_is_valid()
    {
        Assert.True(Validate(BaseRfq() with { Quantities = [QuantityFact(25)] }).IsValid);
    }

    [Fact]
    public void Json_uses_v1_schema_and_expected_field_names()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(BaseRfq()));
        var root = document.RootElement;
        var expected = new[]
        {
            "schema", "version", "rfq_number", "customer_reference", "part_numbers", "revisions",
            "quantities", "quote_due_date", "requested_delivery_date", "material_mentions",
            "process_mentions", "special_requirements", "references_to_previous_jobs", "open_questions"
        };

        Assert.Equal("RFQ_EXTRACTOR", root.GetProperty("schema").GetString());
        Assert.Equal("v1", root.GetProperty("version").GetString());
        Assert.Equal(expected, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["EXPLICIT", "INFERRED", "MISSING", "CONFLICT"],
            Enum.GetNames<RfqFactClassification>());
        Assert.True(root.GetProperty("rfq_number").TryGetProperty("raw_values", out _));
        Assert.True(root.GetProperty("rfq_number").TryGetProperty("normalized_value", out _));
        Assert.True(root.GetProperty("rfq_number").TryGetProperty("source_references", out _));
        Assert.True(root.GetProperty("rfq_number").TryGetProperty("requires_confirmation", out _));
    }

    private static CanonicalRfqValidationResult Validate(CanonicalRfqV1 rfq) =>
        CanonicalRfqValidatorV1.Validate(rfq);

    private static void AssertCode(CanonicalRfqValidationResult result, string code)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == code);
    }

    private static CanonicalRfqV1 BaseRfq() => new(
        Missing<string>(), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
        [], [], [], [], []);

    private static CanonicalRfqFact<string> TextFact(string value) => new([value], value,
        [Source("rfq_file", "opaque-source-id")], 0.99m, RfqFactClassification.EXPLICIT, false, []);

    private static CanonicalRfqFact<int?> QuantityFact(int value) => new([value.ToString()], value,
        [Source("rfq_file", "opaque-source-id")], 0.99m, RfqFactClassification.EXPLICIT, false, []);

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null,
        RfqFactClassification.MISSING, false, []);

    private static RfqSourceReference Source(string type, string id) => new(type, id);
}
