using System.Text;

namespace QuoteEngine.Application.Ai;

public enum AiExecutionDisposition
{
    COMPLETED,
    REVIEW_MANUAL
}

public sealed record AiExternalExecutionRequest(
    AiStructuredRequest Request,
    bool AllowExternalAi,
    IReadOnlyList<string> DocumentTypes);

public sealed record AiRedactionResult(bool IsAllowed, string? NormalizedInputJson, string? Code)
{
    public static AiRedactionResult Allowed(string normalizedInputJson) =>
        new(true, normalizedInputJson, null);

    public static AiRedactionResult Blocked(string code) =>
        new(false, null, code);
}

public interface IAiInputRedactor
{
    AiRedactionResult Redact(string normalizedInputJson);
}

public sealed record AiExecutionPolicyV1(
    IReadOnlySet<string> PermittedUseCases,
    IReadOnlySet<string> PermittedModels,
    IReadOnlySet<string> PermittedDocumentTypes,
    int MaxPayloadBytes);

public sealed record AiPolicyExecutionResult(
    AiExecutionDisposition Disposition,
    string? Code,
    AiGatewayResult? GatewayResult)
{
    public bool IsCompleted => Disposition == AiExecutionDisposition.COMPLETED;
}

public sealed class AiPolicyExecutorV1(
    AiGatewayV1 gateway,
    IAiInputRedactor redactor,
    AiExecutionPolicyV1 policy)
{
    public const string ExternalAiNotAllowedCode = "AI_EXTERNAL_NOT_ALLOWED";
    public const string UseCaseNotPermittedCode = "AI_USE_CASE_NOT_PERMITTED";
    public const string ModelNotPermittedCode = "AI_MODEL_NOT_PERMITTED";
    public const string DocumentTypeRequiredCode = "AI_DOCUMENT_TYPE_REQUIRED";
    public const string DocumentTypeNotPermittedCode = "AI_DOCUMENT_TYPE_NOT_PERMITTED";
    public const string PayloadLimitNotConfiguredCode = "AI_PAYLOAD_LIMIT_NOT_CONFIGURED";
    public const string PayloadTooLargeCode = "AI_PAYLOAD_TOO_LARGE";
    public const string RedactionInvalidCode = "AI_REDACTION_INVALID";
    public const string RedactionNotConfiguredCode = "AI_REDACTION_NOT_CONFIGURED";

    public async Task<AiPolicyExecutionResult> ExecuteAsync(
        AiExternalExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.AllowExternalAi)
            return Review(ExternalAiNotAllowedCode);

        if (!policy.PermittedUseCases.Contains(request.Request.UseCase))
            return Review(UseCaseNotPermittedCode);

        if (!policy.PermittedModels.Contains(request.Request.ModelId))
            return Review(ModelNotPermittedCode);

        if (request.DocumentTypes.Count == 0)
            return Review(DocumentTypeRequiredCode);

        if (request.DocumentTypes.Any(documentType =>
                string.IsNullOrWhiteSpace(documentType)
                || !policy.PermittedDocumentTypes.Contains(documentType)))
            return Review(DocumentTypeNotPermittedCode);

        if (policy.MaxPayloadBytes <= 0)
            return Review(PayloadLimitNotConfiguredCode);

        var normalized = AiInputJsonNormalizerV1.Normalize(request.Request.NormalizedInputJson);
        if (!normalized.IsValid)
            return Review(AiStructuredRequestValidatorV1.InvalidCode);

        if (Utf8Bytes(normalized.NormalizedJson!) > policy.MaxPayloadBytes)
            return Review(PayloadTooLargeCode);

        var redaction = redactor.Redact(normalized.NormalizedJson!);
        if (!redaction.IsAllowed || string.IsNullOrWhiteSpace(redaction.NormalizedInputJson))
            return Review(redaction.Code ?? RedactionInvalidCode);

        var redacted = AiInputJsonNormalizerV1.Normalize(redaction.NormalizedInputJson);
        if (!redacted.IsValid)
            return Review(RedactionInvalidCode);

        if (Utf8Bytes(redacted.NormalizedJson!) > policy.MaxPayloadBytes)
            return Review(PayloadTooLargeCode);

        var providerVisibleRequest = request.Request with
        {
            NormalizedInputJson = redacted.NormalizedJson!
        };

        var gatewayResult = await gateway.ExecuteAsync(providerVisibleRequest, cancellationToken);
        return gatewayResult.IsPass
            ? new(AiExecutionDisposition.COMPLETED, null, gatewayResult)
            : new(AiExecutionDisposition.REVIEW_MANUAL,
                gatewayResult.Code ?? AiGatewayV1.ProviderFailureCode, gatewayResult);
    }

    private static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static AiPolicyExecutionResult Review(string code) =>
        new(AiExecutionDisposition.REVIEW_MANUAL, code, null);
}
