using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public sealed record AiStructuredRequest(string UseCase, string ModelId, string PromptVersion,
    string SchemaVersion, string NormalizedInputJson);

public sealed record AiStructuredRequestValidationResult(bool IsValid, string? Code,
    IReadOnlyList<string> InvalidFields);

public static class AiStructuredRequestValidatorV1
{
    public const string InvalidCode = "AI_REQUEST_INVALID";

    public static AiStructuredRequestValidationResult Validate(AiStructuredRequest? request)
    {
        var invalid = new List<string>();
        if (request is null)
        {
            invalid.Add("request");
            return new(false, InvalidCode, invalid.AsReadOnly());
        }

        Required(request.UseCase, "use_case", invalid);
        Required(request.ModelId, "model_id", invalid);
        Required(request.PromptVersion, "prompt_version", invalid);
        Required(request.SchemaVersion, "schema_version", invalid);
        Required(request.NormalizedInputJson, "normalized_input_json", invalid);
        if (!string.IsNullOrWhiteSpace(request.NormalizedInputJson))
        {
            try
            {
                using var _ = JsonDocument.Parse(request.NormalizedInputJson);
            }
            catch (JsonException)
            {
                invalid.Add("normalized_input_json");
            }
        }
        return invalid.Count == 0
            ? new(true, null, Array.Empty<string>())
            : new(false, InvalidCode, invalid.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void Required(string? value, string field, List<string> invalid)
    {
        if (string.IsNullOrWhiteSpace(value)) invalid.Add(field);
    }
}

public sealed record AiRequestFingerprintResult(bool IsValid, string? Fingerprint, string? Code);

public static class AiRequestFingerprintV1
{
    private const string FingerprintVersion = "ai-structured-request-fingerprint-v1";

    public static AiRequestFingerprintResult Create(AiStructuredRequest? request)
    {
        var validation = AiStructuredRequestValidatorV1.Validate(request);
        if (!validation.IsValid) return new(false, null, validation.Code);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, FingerprintVersion);
        Append(hash, request!.UseCase);
        Append(hash, request.ModelId);
        Append(hash, request.PromptVersion);
        Append(hash, request.SchemaVersion);
        Append(hash, request.NormalizedInputJson);
        return new(true, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), null);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public enum AiOutputGuardStatus
{
    PASS,
    BLOCKED
}

public sealed record RfqExtractorOutputGuardResult(AiOutputGuardStatus Status, string? Code,
    CanonicalRfqV1? Output, IReadOnlyList<CanonicalRfqValidationError> ValidationErrors)
{
    public bool IsPass => Status == AiOutputGuardStatus.PASS;
}

public static class RfqExtractorOutputGuardV1
{
    public const string JsonInvalidCode = "AI_OUTPUT_JSON_INVALID";
    public const string SchemaMismatchCode = "AI_OUTPUT_SCHEMA_MISMATCH";
    public const string ContractInvalidCode = "AI_OUTPUT_CONTRACT_INVALID";

    private static readonly HashSet<string> AllowedTopLevelProperties = new(StringComparer.Ordinal)
    {
        "schema", "version", "rfq_number", "customer_reference", "part_numbers", "revisions",
        "quantities", "quote_due_date", "requested_delivery_date", "material_mentions",
        "process_mentions", "special_requirements", "references_to_previous_jobs", "open_questions"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RfqExtractorOutputGuardResult Evaluate(string? rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson ?? string.Empty);
        }
        catch (JsonException)
        {
            return Blocked(JsonInvalidCode);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Blocked(JsonInvalidCode);
            if (root.EnumerateObject().Any(property => !AllowedTopLevelProperties.Contains(property.Name)))
                return Blocked(ContractInvalidCode);
            if (!HasExactString(root, "schema", CanonicalRfqV1.SchemaName)
                || !HasExactString(root, "version", CanonicalRfqV1.SchemaVersion))
                return Blocked(SchemaMismatchCode);
        }

        CanonicalRfqV1? output;
        try
        {
            output = JsonSerializer.Deserialize<CanonicalRfqV1>(rawJson!, SerializerOptions);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return Blocked(ContractInvalidCode);
        }

        var validation = CanonicalRfqValidatorV1.Validate(output);
        return validation.IsValid
            ? new(AiOutputGuardStatus.PASS, null, output, Array.Empty<CanonicalRfqValidationError>())
            : new(AiOutputGuardStatus.BLOCKED, ContractInvalidCode, null, validation.Errors);
    }

    private static bool HasExactString(JsonElement root, string propertyName, string expected) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && StringComparer.Ordinal.Equals(property.GetString(), expected);

    private static RfqExtractorOutputGuardResult Blocked(string code) =>
        new(AiOutputGuardStatus.BLOCKED, code, null, Array.Empty<CanonicalRfqValidationError>());
}
