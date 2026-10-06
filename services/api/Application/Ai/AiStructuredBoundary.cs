using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public sealed record AiStructuredRequest(string UseCase, string ModelId, string PromptVersion,
    string SchemaVersion, string NormalizedInputJson);

public sealed record AiInputJsonNormalizationResult(bool IsValid, string? NormalizedJson);

public static class AiInputJsonNormalizerV1
{
    public static AiInputJsonNormalizationResult Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new(false, null);
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
            try
            {
                WriteCanonical(writer, document.RootElement);
                writer.Flush();
            }
            catch (InvalidOperationException)
            {
                return new(false, null);
            }

            return new(true, Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != properties.Length)
                    throw new InvalidOperationException("Duplicate JSON property names are not supported.");

                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Unsupported JSON token.");
        }
    }

    private static string NormalizeNumber(string raw)
    {
        var negative = raw[0] == '-';
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? unsigned[..exponentIndex] : unsigned;
        var exponent = exponentIndex >= 0
            ? BigInteger.Parse(unsigned[(exponentIndex + 1)..],
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : BigInteger.Zero;

        var decimalIndex = mantissa.IndexOf('.');
        var integerPart = decimalIndex >= 0 ? mantissa[..decimalIndex] : mantissa;
        var fractionalPart = decimalIndex >= 0 ? mantissa[(decimalIndex + 1)..] : string.Empty;
        var digits = (integerPart + fractionalPart).TrimStart('0');
        if (digits.Length == 0) return "0";

        var scale = exponent - fractionalPart.Length;
        var trailingZeros = 0;
        for (var index = digits.Length - 1; index >= 0 && digits[index] == '0'; index--)
            trailingZeros++;
        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            scale += trailingZeros;
        }

        var sign = negative ? "-" : string.Empty;
        return scale.IsZero
            ? sign + digits
            : sign + digits + "e" + scale.ToString(CultureInfo.InvariantCulture);
    }
}

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
        if (!string.IsNullOrWhiteSpace(request.NormalizedInputJson)
            && !AiInputJsonNormalizerV1.Normalize(request.NormalizedInputJson).IsValid)
            invalid.Add("normalized_input_json");
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
        var normalized = AiInputJsonNormalizerV1.Normalize(request.NormalizedInputJson);
        if (!normalized.IsValid) return new(false, null, AiStructuredRequestValidatorV1.InvalidCode);
        Append(hash, normalized.NormalizedJson!);
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
