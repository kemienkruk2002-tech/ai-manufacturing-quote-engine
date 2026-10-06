using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public interface IAiStructuredProvider
{
    Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
        CancellationToken cancellationToken = default);
}

public enum AiProviderResultStatus
{
    SUCCESS,
    FAILURE
}

public enum AiProviderFailureKind
{
    TRANSIENT,
    PERMANENT,
    CANCELLED,
    UNKNOWN
}

public sealed record AiProviderResult
{
    public AiProviderResultStatus Status { get; private init; }
    public string? RawJson { get; private init; }
    public AiProviderFailureKind? FailureKind { get; private init; }
    public string? FailureCode { get; private init; }

    private AiProviderResult() { }

    public static AiProviderResult Success(string rawJson) => new()
    {
        Status = AiProviderResultStatus.SUCCESS,
        RawJson = rawJson
    };

    public static AiProviderResult Failure(AiProviderFailureKind kind, string code) => new()
    {
        Status = AiProviderResultStatus.FAILURE,
        FailureKind = kind,
        FailureCode = code
    };
}

public sealed record AiGatewayResult(AiOutputGuardStatus Status, string? Code, string? Fingerprint,
    CanonicalRfqV1? Output, AiProviderFailureKind? ProviderFailureKind, string? ProviderFailureCode,
    IReadOnlyList<CanonicalRfqValidationError> ValidationErrors, string? RawProviderJson = null)
{
    public bool IsPass => Status == AiOutputGuardStatus.PASS;
}

public sealed class AiGatewayV1(IAiStructuredProvider provider)
{
    public const string UseCaseNotSupportedCode = "AI_USE_CASE_NOT_SUPPORTED";
    public const string ProviderFailureCode = "AI_PROVIDER_FAILURE";

    public async Task<AiGatewayResult> ExecuteAsync(AiStructuredRequest request,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = AiRequestFingerprintV1.Create(request);
        if (!fingerprint.IsValid)
            return Blocked(fingerprint.Code ?? AiStructuredRequestValidatorV1.InvalidCode);

        if (!StringComparer.Ordinal.Equals(request.UseCase, "rfq-extractor")
            || !StringComparer.Ordinal.Equals(request.SchemaVersion, "v1"))
            return Blocked(UseCaseNotSupportedCode, fingerprint.Fingerprint);

        var providerResult = await provider.ExecuteAsync(request, cancellationToken);
        if (providerResult.Status == AiProviderResultStatus.FAILURE)
            return new(AiOutputGuardStatus.BLOCKED, ProviderFailureCode, fingerprint.Fingerprint, null,
                providerResult.FailureKind, providerResult.FailureCode,
                Array.Empty<CanonicalRfqValidationError>());

        var guard = RfqExtractorOutputGuardV1.Evaluate(providerResult.RawJson);
        return new(guard.Status, guard.Code, fingerprint.Fingerprint, guard.Output, null, null,
            guard.ValidationErrors, providerResult.RawJson);
    }

    private static AiGatewayResult Blocked(string code, string? fingerprint = null) =>
        new(AiOutputGuardStatus.BLOCKED, code, fingerprint, null, null, null,
            Array.Empty<CanonicalRfqValidationError>());
}
