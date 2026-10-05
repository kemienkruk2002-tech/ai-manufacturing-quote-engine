using System.Text.Json.Serialization;

namespace QuoteEngine.Domain.Quoting;

[JsonConverter(typeof(JsonStringEnumConverter<RfqFactClassification>))]
public enum RfqFactClassification
{
    EXPLICIT,
    INFERRED,
    MISSING,
    CONFLICT
}

public sealed record RfqSourceReference(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("page")] int? Page = null,
    [property: JsonPropertyName("locator")] string? Locator = null);

public sealed record CanonicalRfqFact<T>(
    [property: JsonPropertyName("raw_values")] IReadOnlyList<string> RawValues,
    [property: JsonPropertyName("normalized_value")] T? NormalizedValue,
    [property: JsonPropertyName("source_references")] IReadOnlyList<RfqSourceReference> SourceReferences,
    [property: JsonPropertyName("model_confidence")] decimal? ModelConfidence,
    [property: JsonPropertyName("classification")] RfqFactClassification Classification,
    [property: JsonPropertyName("requires_confirmation")] bool RequiresConfirmation,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record CanonicalRfqV1(
    [property: JsonPropertyName("rfq_number")] CanonicalRfqFact<string> RfqNumber,
    [property: JsonPropertyName("customer_reference")] CanonicalRfqFact<string> CustomerReference,
    [property: JsonPropertyName("part_numbers")] IReadOnlyList<CanonicalRfqFact<string>> PartNumbers,
    [property: JsonPropertyName("revisions")] IReadOnlyList<CanonicalRfqFact<string>> Revisions,
    [property: JsonPropertyName("quantities")] IReadOnlyList<CanonicalRfqFact<int?>> Quantities,
    [property: JsonPropertyName("quote_due_date")] CanonicalRfqFact<DateOnly?> QuoteDueDate,
    [property: JsonPropertyName("requested_delivery_date")] CanonicalRfqFact<DateOnly?> RequestedDeliveryDate,
    [property: JsonPropertyName("material_mentions")] IReadOnlyList<CanonicalRfqFact<string>> MaterialMentions,
    [property: JsonPropertyName("process_mentions")] IReadOnlyList<CanonicalRfqFact<string>> ProcessMentions,
    [property: JsonPropertyName("special_requirements")] IReadOnlyList<CanonicalRfqFact<string>> SpecialRequirements,
    [property: JsonPropertyName("references_to_previous_jobs")] IReadOnlyList<CanonicalRfqFact<string>> ReferencesToPreviousJobs,
    [property: JsonPropertyName("open_questions")] IReadOnlyList<CanonicalRfqFact<string>> OpenQuestions)
{
    public const string SchemaName = "RFQ_EXTRACTOR";
    public const string SchemaVersion = "v1";

    [JsonPropertyName("schema")]
    [JsonPropertyOrder(-2)]
    public string Schema => SchemaName;

    [JsonPropertyName("version")]
    [JsonPropertyOrder(-1)]
    public string Version => SchemaVersion;
}

public sealed record CanonicalRfqValidationError(string Code, string Path);

public sealed record CanonicalRfqValidationResult(IReadOnlyList<CanonicalRfqValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class CanonicalRfqValidatorV1
{
    public static CanonicalRfqValidationResult Validate(CanonicalRfqV1? rfq)
    {
        var errors = new List<CanonicalRfqValidationError>();
        if (rfq is null)
        {
            errors.Add(new("CANONICAL_RFQ_REQUIRED", "$"));
            return new(errors.AsReadOnly());
        }

        ValidateFact(rfq.RfqNumber, "rfq_number", errors);
        ValidateFact(rfq.CustomerReference, "customer_reference", errors);
        ValidateFacts(rfq.PartNumbers, "part_numbers", errors);
        ValidateFacts(rfq.Revisions, "revisions", errors);
        ValidateFacts(rfq.Quantities, "quantities", errors, quantity: true);
        ValidateFact(rfq.QuoteDueDate, "quote_due_date", errors);
        ValidateFact(rfq.RequestedDeliveryDate, "requested_delivery_date", errors);
        ValidateFacts(rfq.MaterialMentions, "material_mentions", errors);
        ValidateFacts(rfq.ProcessMentions, "process_mentions", errors);
        ValidateFacts(rfq.SpecialRequirements, "special_requirements", errors);
        ValidateFacts(rfq.ReferencesToPreviousJobs, "references_to_previous_jobs", errors);
        ValidateFacts(rfq.OpenQuestions, "open_questions", errors);
        return new(errors.AsReadOnly());
    }

    private static void ValidateFacts<T>(IReadOnlyList<CanonicalRfqFact<T>>? facts, string path,
        List<CanonicalRfqValidationError> errors, bool quantity = false)
    {
        if (facts is null)
        {
            errors.Add(new("RFQ_FACT_COLLECTION_REQUIRED", path));
            return;
        }
        for (var index = 0; index < facts.Count; index++)
            ValidateFact(facts[index], $"{path}[{index}]", errors, quantity);
    }

    private static void ValidateFact<T>(CanonicalRfqFact<T>? fact, string path,
        List<CanonicalRfqValidationError> errors, bool quantity = false)
    {
        if (fact is null)
        {
            errors.Add(new("RFQ_FACT_REQUIRED", path));
            return;
        }

        var rawCount = fact.RawValues?.Count ?? 0;
        var sourceCount = fact.SourceReferences?.Count ?? 0;
        var hasNormalizedValue = fact.NormalizedValue is not null;

        if (fact.ModelConfidence is < 0 or > 1)
            errors.Add(new("RFQ_FACT_CONFIDENCE_OUT_OF_RANGE", path));
        if (fact.SourceReferences is not null)
        {
            for (var index = 0; index < fact.SourceReferences.Count; index++)
            {
                var source = fact.SourceReferences[index];
                if (source is null || string.IsNullOrWhiteSpace(source.SourceType))
                    errors.Add(new("RFQ_SOURCE_TYPE_REQUIRED", $"{path}.source_references[{index}]"));
                if (source is null || string.IsNullOrWhiteSpace(source.SourceId))
                    errors.Add(new("RFQ_SOURCE_ID_REQUIRED", $"{path}.source_references[{index}]"));
            }
        }

        switch (fact.Classification)
        {
            case RfqFactClassification.EXPLICIT:
                if (rawCount < 1) errors.Add(new("RFQ_EXPLICIT_RAW_VALUE_REQUIRED", path));
                if (sourceCount < 1) errors.Add(new("RFQ_EXPLICIT_SOURCE_REQUIRED", path));
                break;
            case RfqFactClassification.INFERRED:
                if (!fact.RequiresConfirmation) errors.Add(new("RFQ_INFERRED_CONFIRMATION_REQUIRED", path));
                break;
            case RfqFactClassification.MISSING:
                if (hasNormalizedValue) errors.Add(new("RFQ_MISSING_NORMALIZED_VALUE_FORBIDDEN", path));
                break;
            case RfqFactClassification.CONFLICT:
                if (rawCount < 2) errors.Add(new("RFQ_CONFLICT_RAW_VALUES_REQUIRED", path));
                if (sourceCount < 2) errors.Add(new("RFQ_CONFLICT_SOURCES_REQUIRED", path));
                if (!fact.RequiresConfirmation) errors.Add(new("RFQ_CONFLICT_CONFIRMATION_REQUIRED", path));
                break;
        }

        if (quantity && fact.NormalizedValue is int value && value <= 0)
            errors.Add(new("RFQ_QUANTITY_MUST_BE_POSITIVE", path));
    }
}
