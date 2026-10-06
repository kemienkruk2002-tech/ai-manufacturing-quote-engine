using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public sealed record RfqExtractionSourceSelection(string LogicalKey, int VersionNo, string DocumentType);
public sealed record RfqExtractionSourceMaterializationResult(bool IsSuccess, string? Content, string? Code)
{
    public static RfqExtractionSourceMaterializationResult Success(string content) => new(true, content, null);
    public static RfqExtractionSourceMaterializationResult Failure(string code) => new(false, null, code);
}
public interface IRfqExtractionSourceMaterializer
{
    Task<RfqExtractionSourceMaterializationResult> MaterializeAsync(RfqFileVersion source, string documentType,
        CancellationToken cancellationToken = default);
}
public sealed record RfqExtractionServiceRequest(Guid TenantId, Guid QuoteRequestId, string ModelId,
    bool AllowExternalAi, IReadOnlyList<RfqExtractionSourceSelection> Sources);
public sealed record RfqExtractionServiceResult(AiExecutionDisposition Disposition, string? Code,
    string? RequestFingerprint, string ModelId, string PromptVersion, string SchemaVersion,
    AiGatewayResult? GatewayResult, StoredRfqExtractionExecution? StoredExecution,
    RfqExtractionPersistenceResult? Persistence = null);

public sealed class RfqExtractionServiceV1(
    IRfqFileRepository files,
    IRfqExtractionSourceMaterializer materializer,
    AiPolicyExecutorV1 policyExecutor,
    IRfqExtractionExecutionRepository executions)
{
    public const string SourceRequiredCode = "RFQ_EXTRACTION_SOURCE_REQUIRED";
    public const string SourceNotFoundCode = "RFQ_EXTRACTION_SOURCE_NOT_FOUND";
    public const string SourceMaterializationFailedCode = "RFQ_EXTRACTION_SOURCE_MATERIALIZATION_FAILED";
    public const string SourceMaterializerNotConfiguredCode = "RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED";
    public const string DuplicateSourceCode = "RFQ_EXTRACTION_SOURCE_DUPLICATE";
    public const string RfqNotFoundCode = "RFQ_EXTRACTION_RFQ_NOT_FOUND";

    public async Task<RfqExtractionServiceResult> ExecuteAsync(RfqExtractionServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(request);
        if (!await files.RequestExistsAsync(request.TenantId, request.QuoteRequestId, cancellationToken))
            throw new DomainValidationException(RfqNotFoundCode, "The RFQ does not exist in the requested tenant.");
        if (request.Sources.Count == 0)
            return await PersistReviewAsync(request, SourceRequiredCode, null, "[]", cancellationToken);
        ValidateSelections(request.Sources);
        var selected = request.Sources.OrderBy(x => x.LogicalKey, StringComparer.Ordinal).ThenBy(x => x.VersionNo)
            .ThenBy(x => x.DocumentType, StringComparer.Ordinal).ToArray();
        var requestedLineage = SerializeRequestedLineage(selected);
        var normalizedSources = new List<object>(selected.Length);
        var resolvedLineage = new List<object>(selected.Length);
        foreach (var selection in selected)
        {
            var version = await files.FindVersionAsync(request.TenantId, request.QuoteRequestId,
                selection.LogicalKey, selection.VersionNo, cancellationToken);
            if (version is null)
                return await PersistReviewAsync(request, SourceNotFoundCode, null, requestedLineage, cancellationToken);
            resolvedLineage.Add(new { logical_key = version.LogicalKey, version_no = version.VersionNo,
                document_type = selection.DocumentType, sha256 = version.Sha256, source_reference = version.SourceReference });
            var materialized = await materializer.MaterializeAsync(version, selection.DocumentType, cancellationToken);
            if (!materialized.IsSuccess || materialized.Content is null)
                return await PersistReviewAsync(request,
                    string.IsNullOrWhiteSpace(materialized.Code) ? SourceMaterializationFailedCode : materialized.Code,
                    null, JsonSerializer.Serialize(resolvedLineage), cancellationToken);
            normalizedSources.Add(new { source_key = $"{version.LogicalKey}:v{version.VersionNo}",
                source_reference = version.SourceReference, logical_key = version.LogicalKey,
                version_no = version.VersionNo, document_type = selection.DocumentType,
                sha256 = version.Sha256, content = materialized.Content });
        }
        var sourceLineage = JsonSerializer.Serialize(resolvedLineage);
        var normalizedInput = AiInputJsonNormalizerV1.Normalize(JsonSerializer.Serialize(new { sources = normalizedSources }));
        if (!normalizedInput.IsValid) throw new InvalidOperationException("RFQ extraction input normalization failed.");
        var aiRequest = new AiStructuredRequest(RfqExtractorPromptV1.UseCase, request.ModelId,
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion, normalizedInput.NormalizedJson!);
        var fingerprint = AiRequestFingerprintV1.Create(aiRequest);
        if (!fingerprint.IsValid || fingerprint.Fingerprint is null)
            throw new InvalidOperationException("RFQ extraction fingerprint generation failed.");
        var execution = await policyExecutor.ExecuteAsync(new(aiRequest, request.AllowExternalAi,
            selected.Select(x => x.DocumentType).ToArray()), cancellationToken);
        var effectiveFingerprint = execution.GatewayResult?.Fingerprint ?? fingerprint.Fingerprint;
        var stored = await executions.SaveAsync(new(request.TenantId, request.QuoteRequestId, request.ModelId,
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion, effectiveFingerprint,
            execution.Disposition, execution.Code), cancellationToken);
        var rawProviderOutput = execution.GatewayResult?.RawProviderJson;
        var canonicalDraft = execution.Disposition == AiExecutionDisposition.COMPLETED
            && execution.GatewayResult is { IsPass: true } ? rawProviderOutput : null;
        var persisted = await executions.SaveAttemptAsync(new(request.TenantId, request.QuoteRequestId,
            request.ModelId, RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion,
            effectiveFingerprint, execution.Disposition, execution.Code, sourceLineage,
            rawProviderOutput, canonicalDraft), cancellationToken);
        return new(execution.Disposition, execution.Code, effectiveFingerprint, request.ModelId,
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion,
            execution.GatewayResult, stored, persisted);
    }

    private async Task<RfqExtractionServiceResult> PersistReviewAsync(RfqExtractionServiceRequest request,
        string code, string? fingerprint, string sourceLineage, CancellationToken cancellationToken)
    {
        var stored = await executions.SaveAsync(new(request.TenantId, request.QuoteRequestId, request.ModelId,
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion, fingerprint,
            AiExecutionDisposition.REVIEW_MANUAL, code), cancellationToken);
        var persisted = await executions.SaveAttemptAsync(new(request.TenantId, request.QuoteRequestId,
            request.ModelId, RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion,
            fingerprint, AiExecutionDisposition.REVIEW_MANUAL, code, sourceLineage, null, null), cancellationToken);
        return new(AiExecutionDisposition.REVIEW_MANUAL, code, fingerprint, request.ModelId,
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion, null, stored, persisted);
    }

    private static string SerializeRequestedLineage(IEnumerable<RfqExtractionSourceSelection> sources) =>
        JsonSerializer.Serialize(sources.Select(x => new { logical_key = x.LogicalKey, version_no = x.VersionNo,
            document_type = x.DocumentType }));

    private static void ValidateIdentity(RfqExtractionServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId == Guid.Empty || request.QuoteRequestId == Guid.Empty)
            throw new DomainValidationException("RFQ_EXTRACTION_IDENTITY_INVALID", "Tenant and RFQ identifiers are required.");
        if (string.IsNullOrWhiteSpace(request.ModelId))
            throw new DomainValidationException("RFQ_EXTRACTION_MODEL_INVALID", "Model ID is required.");
        ArgumentNullException.ThrowIfNull(request.Sources);
    }

    private static void ValidateSelections(IReadOnlyList<RfqExtractionSourceSelection> selections)
    {
        foreach (var selection in selections)
            if (string.IsNullOrWhiteSpace(selection.LogicalKey) || selection.VersionNo <= 0
                || string.IsNullOrWhiteSpace(selection.DocumentType))
                throw new DomainValidationException("RFQ_EXTRACTION_SOURCE_INVALID",
                    "Every selected source requires a logical key, positive version and document type.");
        if (selections.GroupBy(x => (x.LogicalKey, x.VersionNo)).Any(group => group.Count() > 1))
            throw new DomainValidationException(DuplicateSourceCode,
                "The same RFQ file version cannot be selected more than once.");
    }
}
