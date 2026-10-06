using System.Text.Json;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application;

public enum RfqReviewAction { CONFIRM, REJECT, CORRECT }

public sealed record RfqReviewWrite(Guid TenantId, Guid QuoteRequestId, long ExpectedDraftRowVersion,
    string FieldPath, RfqReviewAction Action, string? CorrectedValueJson, string Actor, string Source, string Reason);
public sealed record StoredRfqReview(Guid Id, Guid TenantId, Guid QuoteRequestId, Guid SourceAttemptId,
    long DraftRowVersion, string FieldPath, RfqReviewAction Action, string? CorrectedValueJson,
    string Actor, string Source, string Reason, DateTimeOffset CreatedAt);
public sealed record RfqReviewBlocker(string FieldPath, RfqFactClassification Classification);
public sealed record RfqReviewReadiness(bool CanProgress, IReadOnlyList<RfqReviewBlocker> Blockers);

public interface IRfqReviewRepository
{
    Task<StoredRfqReview?> AppendAsync(RfqReviewWrite write, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredRfqReview>> ListAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default);
}

public sealed class RfqReviewServiceV1(IRfqExtractionExecutionRepository extractionRepository, IRfqReviewRepository reviewRepository)
{
    public async Task<StoredRfqReview?> ReviewAsync(RfqReviewWrite write, CancellationToken token = default)
    {
        ValidateWrite(write);
        var draft = await extractionRepository.FindCurrentDraftAsync(write.TenantId, write.QuoteRequestId, token);
        if (draft is null) throw new DomainValidationException("RFQ_REVIEW_DRAFT_NOT_FOUND", "A current CanonicalRFQ draft is required.");
        if (draft.RowVersion != write.ExpectedDraftRowVersion) return null;
        var fact = FindFact(draft.CanonicalJson, write.FieldPath);
        if (fact is null) throw new DomainValidationException("RFQ_REVIEW_FIELD_NOT_FOUND", "The reviewed field path does not identify a CanonicalRFQ fact.");
        if (write.Action == RfqReviewAction.CONFIRM && !HasNormalizedValue(fact.Value))
            throw new DomainValidationException("RFQ_REVIEW_CONFIRM_VALUE_REQUIRED", "A fact without a normalized value cannot be confirmed.");
        if (write.Action == RfqReviewAction.CORRECT) ValidateCorrection(write.CorrectedValueJson);
        return await reviewRepository.AppendAsync(write, token);
    }

    public async Task<RfqReviewReadiness> GetReadinessAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken token = default)
    {
        var draft = await extractionRepository.FindCurrentDraftAsync(tenantId, quoteRequestId, token);
        if (draft is null) throw new DomainValidationException("RFQ_REVIEW_DRAFT_NOT_FOUND", "A current CanonicalRFQ draft is required.");
        var reviews = await reviewRepository.ListAsync(tenantId, quoteRequestId, token);
        var latest = reviews.Where(x => x.DraftRowVersion == draft.RowVersion)
            .GroupBy(x => x.FieldPath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Last(), StringComparer.Ordinal);
        var blockers = EnumerateCriticalFacts(draft.CanonicalJson)
            .Where(x => !latest.TryGetValue(x.FieldPath, out var review)
                || review.Action == RfqReviewAction.REJECT
                || (review.Action == RfqReviewAction.CONFIRM && x.Classification == RfqFactClassification.MISSING))
            .ToArray();
        return new(blockers.Length == 0, blockers);
    }

    private static void ValidateWrite(RfqReviewWrite write)
    {
        if (write.TenantId == Guid.Empty || write.QuoteRequestId == Guid.Empty || write.ExpectedDraftRowVersion < 1)
            throw new DomainValidationException("RFQ_REVIEW_IDENTITY_INVALID", "Tenant, RFQ and positive draft row version are required.");
        if (string.IsNullOrWhiteSpace(write.FieldPath) || string.IsNullOrWhiteSpace(write.Actor)
            || string.IsNullOrWhiteSpace(write.Source) || string.IsNullOrWhiteSpace(write.Reason))
            throw new DomainValidationException("RFQ_REVIEW_AUDIT_REQUIRED", "Field path, actor, source and reason are required.");
        if (write.Action != RfqReviewAction.CORRECT && write.CorrectedValueJson is not null)
            throw new DomainValidationException("RFQ_REVIEW_CORRECTION_UNEXPECTED", "Only CORRECT may include corrected_value.");
    }

    private static void ValidateCorrection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DomainValidationException("RFQ_REVIEW_CORRECTION_REQUIRED", "CORRECT requires corrected_value.");
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw new DomainValidationException("RFQ_REVIEW_CORRECTION_INVALID", "corrected_value must be valid JSON."); }
    }

    private static JsonElement? FindFact(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryResolve(root, path, out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("classification", out _)) return null;
        return value.Clone();
    }

    private static bool HasNormalizedValue(JsonElement fact) =>
        fact.TryGetProperty("normalized_value", out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static IEnumerable<RfqReviewBlocker> EnumerateCriticalFacts(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var item in Walk(document.RootElement, "")) yield return item;
    }

    private static IEnumerable<RfqReviewBlocker> Walk(JsonElement node, string path)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("classification", out var classification)
            && Enum.TryParse<RfqFactClassification>(classification.GetString(), false, out var parsed)
            && parsed is RfqFactClassification.MISSING or RfqFactClassification.CONFLICT)
        {
            yield return new(path, parsed); yield break;
        }
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
                foreach (var item in Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}")) yield return item;
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in node.EnumerateArray())
            { foreach (var item in Walk(child, $"{path}[{index}]")) yield return item; index++; }
        }
    }

    private static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var i = 0;
        while (i < path.Length)
        {
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            if (i > start && (!value.TryGetProperty(path[start..i], out value))) return false;
            if (i < path.Length && path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1); if (end < 0 || !int.TryParse(path[(i + 1)..end], out var index)
                    || value.ValueKind != JsonValueKind.Array || index < 0 || index >= value.GetArrayLength()) return false;
                value = value[index]; i = end + 1;
            }
            if (i < path.Length && path[i] == '.') i++;
        }
        return true;
    }
}
