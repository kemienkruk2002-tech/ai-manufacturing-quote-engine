using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Application.Ai;

public sealed record RfqExtractionIdempotencyIdentity(
    string Version,
    string Key,
    string RequestHash);

public static class RfqExtractionIdempotencyV1
{
    public const string Version = "rfq-extraction-idempotency-v1";
    public const int MaxKeyLength = 200;

    public static RfqExtractionIdempotencyIdentity Create(
        string key,
        Guid tenantId,
        Guid quoteRequestId,
        string modelId,
        bool allowExternalAi,
        IReadOnlyList<RfqExtractionSourceSelection> sources)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || key.Any(char.IsControl))
            throw new DomainValidationException(
                "RFQ_EXTRACTION_IDEMPOTENCY_KEY_INVALID",
                $"Idempotency key must contain 1-{MaxKeyLength} non-control characters.");
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty)
            throw new DomainValidationException(
                "RFQ_EXTRACTION_IDEMPOTENCY_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required for extraction idempotency.");
        if (string.IsNullOrWhiteSpace(modelId))
            throw new DomainValidationException(
                "RFQ_EXTRACTION_IDEMPOTENCY_MODEL_INVALID",
                "Model ID is required for extraction idempotency.");
        ArgumentNullException.ThrowIfNull(sources);

        var orderedSources = sources
            .OrderBy(source => source.LogicalKey, StringComparer.Ordinal)
            .ThenBy(source => source.VersionNo)
            .ThenBy(source => source.DocumentType, StringComparer.Ordinal)
            .Select(source => new
            {
                logical_key = source.LogicalKey,
                version_no = source.VersionNo,
                document_type = source.DocumentType
            })
            .ToArray();

        var canonical = AiInputJsonNormalizerV1.Normalize(JsonSerializer.Serialize(new
        {
            version = Version,
            tenant_id = tenantId,
            quote_request_id = quoteRequestId,
            model_id = modelId,
            allow_external_ai = allowExternalAi,
            sources = orderedSources
        }));
        if (!canonical.IsValid || canonical.NormalizedJson is null)
            throw new InvalidOperationException("Extraction idempotency request normalization failed.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.NormalizedJson)))
            .ToLowerInvariant();
        return new(Version, key, hash);
    }
}
