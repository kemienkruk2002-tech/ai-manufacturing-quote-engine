using System.Text.Json;
using System.Text.Json.Nodes;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public sealed record RfqCanonicalFieldReviewRequest(
    Guid TenantId,
    Guid QuoteRequestId,
    string FieldPath,
    RfqCanonicalReviewDecision Decision,
    JsonElement? CorrectedFact,
    long ExpectedRowVersion,
    string Actor,
    string ReviewSource,
    string Reason,
    string? CorrelationId);

public sealed record RfqCanonicalFieldReviewResult(
    RfqCanonicalReviewApplyStatus Status,
    StoredRfqCanonicalDraft? Draft,
    StoredRfqCanonicalReviewEvent? ReviewEvent);

public sealed record RfqCanonicalReviewBlocker(string FieldPath, string Code);

public sealed record RfqCanonicalReviewReadiness(
    Guid TenantId,
    Guid QuoteRequestId,
    Guid SourceAttemptId,
    long RowVersion,
    bool IsReviewComplete,
    IReadOnlyList<RfqCanonicalReviewBlocker> Blockers);

public sealed class RfqCanonicalReviewServiceV1(
    IRfqExtractionExecutionRepository extractionRepository,
    IRfqCanonicalReviewRepository reviewRepository)
{
    public const string FieldPathInvalidCode = "RFQ_CANONICAL_REVIEW_FIELD_PATH_INVALID";
    public const string CorrectionRequiredCode = "RFQ_CANONICAL_REVIEW_CORRECTION_REQUIRED";
    public const string CorrectionForbiddenCode = "RFQ_CANONICAL_REVIEW_CORRECTION_FORBIDDEN";
    public const string CorrectionInvalidCode = "RFQ_CANONICAL_REVIEW_CORRECTION_INVALID";
    public const string ActorRequiredCode = "RFQ_CANONICAL_REVIEW_ACTOR_REQUIRED";
    public const string SourceRequiredCode = "RFQ_CANONICAL_REVIEW_SOURCE_REQUIRED";
    public const string ReasonRequiredCode = "RFQ_CANONICAL_REVIEW_REASON_REQUIRED";
    public const string VersionInvalidCode = "RFQ_CANONICAL_REVIEW_VERSION_INVALID";

    public const string MissingBlockerCode = "RFQ_CANONICAL_REVIEW_MISSING";
    public const string ConflictBlockerCode = "RFQ_CANONICAL_REVIEW_CONFLICT";
    public const string RejectedBlockerCode = "RFQ_CANONICAL_REVIEW_REJECTED";

    private static readonly HashSet<string> ScalarFactNames = new(StringComparer.Ordinal)
    {
        "rfq_number",
        "customer_reference",
        "quote_due_date",
        "requested_delivery_date"
    };

    private static readonly HashSet<string> CollectionFactNames = new(StringComparer.Ordinal)
    {
        "part_numbers",
        "revisions",
        "quantities",
        "material_mentions",
        "process_mentions",
        "special_requirements",
        "references_to_previous_jobs",
        "open_questions"
    };

    public async Task<RfqCanonicalFieldReviewResult> ReviewAsync(
        RfqCanonicalFieldReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var current = await extractionRepository.FindCurrentDraftAsync(
            request.TenantId, request.QuoteRequestId, cancellationToken);
        if (current is null)
            return new(RfqCanonicalReviewApplyStatus.DRAFT_NOT_FOUND, null, null);

        var root = JsonNode.Parse(current.CanonicalJson) as JsonObject
            ?? throw new InvalidOperationException("Stored CanonicalRFQ draft is not a JSON object.");
        if (!TryResolveFact(root, request.FieldPath, out var location))
            throw Validation(FieldPathInvalidCode,
                "Field path must identify one existing top-level CanonicalRFQ fact.");

        var beforeFactJson = location.Current.ToJsonString();
        var afterFactJson = beforeFactJson;
        var canonicalJson = current.CanonicalJson;

        if (request.Decision == RfqCanonicalReviewDecision.CORRECT)
        {
            if (request.CorrectedFact is not { } corrected)
                throw Validation(CorrectionRequiredCode,
                    "CORRECT requires a complete replacement CanonicalRFQ fact.");
            if (corrected.ValueKind != JsonValueKind.Object)
                throw Validation(CorrectionInvalidCode,
                    "Corrected fact must be a JSON object.");

            var replacement = JsonNode.Parse(corrected.GetRawText()) as JsonObject
                ?? throw Validation(CorrectionInvalidCode,
                    "Corrected fact must be a JSON object.");
            location.Replace(replacement);
            afterFactJson = replacement.ToJsonString();
            canonicalJson = root.ToJsonString();

            var guard = RfqExtractorOutputGuardV1.Evaluate(canonicalJson);
            if (!guard.IsPass)
                throw Validation(CorrectionInvalidCode,
                    "Correction must preserve the existing CanonicalRFQ v1 contract.");
        }
        else if (request.CorrectedFact is not null)
        {
            throw Validation(CorrectionForbiddenCode,
                "Only CORRECT may include a replacement CanonicalRFQ fact.");
        }

        var applied = await reviewRepository.ApplyAsync(new(
            request.TenantId,
            request.QuoteRequestId,
            request.FieldPath,
            request.Decision,
            canonicalJson,
            beforeFactJson,
            afterFactJson,
            request.ExpectedRowVersion,
            request.Actor,
            request.ReviewSource,
            request.Reason,
            request.CorrelationId), cancellationToken);

        return new(applied.Status, applied.Draft, applied.ReviewEvent);
    }

    public async Task<RfqCanonicalReviewReadiness?> GetReadinessAsync(
        Guid tenantId,
        Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, quoteRequestId);
        var draft = await extractionRepository.FindCurrentDraftAsync(
            tenantId, quoteRequestId, cancellationToken);
        if (draft is null) return null;

        var guard = RfqExtractorOutputGuardV1.Evaluate(draft.CanonicalJson);
        if (!guard.IsPass || guard.Output is null)
            throw new InvalidOperationException("Stored CanonicalRFQ draft failed its own v1 contract.");

        var blockers = new Dictionary<string, RfqCanonicalReviewBlocker>(StringComparer.Ordinal);
        AddFactBlocker(blockers, "rfq_number", guard.Output.RfqNumber);
        AddFactBlocker(blockers, "customer_reference", guard.Output.CustomerReference);
        AddFactBlockers(blockers, "part_numbers", guard.Output.PartNumbers);
        AddFactBlockers(blockers, "revisions", guard.Output.Revisions);
        AddFactBlockers(blockers, "quantities", guard.Output.Quantities);
        AddFactBlocker(blockers, "quote_due_date", guard.Output.QuoteDueDate);
        AddFactBlocker(blockers, "requested_delivery_date", guard.Output.RequestedDeliveryDate);
        AddFactBlockers(blockers, "material_mentions", guard.Output.MaterialMentions);
        AddFactBlockers(blockers, "process_mentions", guard.Output.ProcessMentions);
        AddFactBlockers(blockers, "special_requirements", guard.Output.SpecialRequirements);
        AddFactBlockers(blockers, "references_to_previous_jobs", guard.Output.ReferencesToPreviousJobs);
        AddFactBlockers(blockers, "open_questions", guard.Output.OpenQuestions);

        var reviews = await reviewRepository.ListAsync(tenantId, quoteRequestId, cancellationToken);
        foreach (var group in reviews.GroupBy(item => item.FieldPath, StringComparer.Ordinal))
        {
            var latest = group.Last();
            if (latest.Decision == RfqCanonicalReviewDecision.REJECT
                && !blockers.ContainsKey(latest.FieldPath))
                blockers[latest.FieldPath] = new(latest.FieldPath, RejectedBlockerCode);
        }

        var ordered = blockers.Values
            .OrderBy(item => item.FieldPath, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();

        return new(
            tenantId,
            quoteRequestId,
            draft.SourceAttemptId,
            draft.RowVersion,
            ordered.Length == 0,
            ordered);
    }

    private static void ValidateRequest(RfqCanonicalFieldReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.TenantId, request.QuoteRequestId);
        if (request.ExpectedRowVersion < 1)
            throw Validation(VersionInvalidCode, "Expected row version must be positive.");
        if (string.IsNullOrWhiteSpace(request.Actor))
            throw Validation(ActorRequiredCode, "Authenticated review actor is required.");
        if (string.IsNullOrWhiteSpace(request.ReviewSource))
            throw Validation(SourceRequiredCode, "Review source is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw Validation(ReasonRequiredCode, "Review reason is required.");
        if (string.IsNullOrWhiteSpace(request.FieldPath))
            throw Validation(FieldPathInvalidCode, "Field path is required.");
    }

    private static void ValidateIdentity(Guid tenantId, Guid quoteRequestId)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty)
            throw Validation("RFQ_CANONICAL_REVIEW_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required.");
    }

    private static DomainValidationException Validation(string code, string message) =>
        new(code, message);

    private static void AddFactBlockers<T>(
        IDictionary<string, RfqCanonicalReviewBlocker> blockers,
        string name,
        IReadOnlyList<CanonicalRfqFact<T>> facts)
    {
        for (var index = 0; index < facts.Count; index++)
            AddFactBlocker(blockers, $"{name}[{index}]", facts[index]);
    }

    private static void AddFactBlocker<T>(
        IDictionary<string, RfqCanonicalReviewBlocker> blockers,
        string path,
        CanonicalRfqFact<T> fact)
    {
        var code = fact.Classification switch
        {
            RfqFactClassification.MISSING => MissingBlockerCode,
            RfqFactClassification.CONFLICT => ConflictBlockerCode,
            _ => null
        };
        if (code is not null)
            blockers[path] = new(path, code);
    }

    private static bool TryResolveFact(
        JsonObject root,
        string path,
        out FactLocation location)
    {
        if (ScalarFactNames.Contains(path)
            && root[path] is JsonObject scalar)
        {
            location = new(scalar, replacement => root[path] = replacement);
            return true;
        }

        foreach (var name in CollectionFactNames)
        {
            var prefix = name + "[";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)
                || !path.EndsWith(']'))
                continue;

            var indexText = path[prefix.Length..^1];
            if (!int.TryParse(indexText, out var index) || index < 0)
                break;
            if (root[name] is not JsonArray array
                || index >= array.Count
                || array[index] is not JsonObject item)
                break;

            location = new(item, replacement => array[index] = replacement);
            return true;
        }

        location = null!;
        return false;
    }

    private sealed record FactLocation(JsonObject Current, Action<JsonObject> Replace);
}
