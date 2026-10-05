namespace QuoteEngine.Application.Ai;

public interface IAiRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAiRetryDelay : IAiRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class RetryingAiStructuredProviderV1 : IAiStructuredProvider
{
    public const string RetryCancelledCode = "AI_RETRY_CANCELLED";

    private readonly IAiStructuredProvider _inner;
    private readonly TimeSpan[] _retryDelays;
    private readonly IAiRetryDelay _delay;

    public RetryingAiStructuredProviderV1(IAiStructuredProvider inner,
        IReadOnlyList<TimeSpan> retryDelays,
        IAiRetryDelay delay)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(retryDelays);
        ArgumentNullException.ThrowIfNull(delay);

        if (retryDelays.Any(value => value < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(retryDelays),
                "Retry delays must be greater than or equal to TimeSpan.Zero.");

        _inner = inner;
        _retryDelays = retryDelays.ToArray();
        _delay = delay;
    }

    public async Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        for (var retryIndex = 0; ; retryIndex++)
        {
            if (result.Status != AiProviderResultStatus.FAILURE
                || result.FailureKind != AiProviderFailureKind.TRANSIENT
                || retryIndex >= _retryDelays.Length)
                return result;

            try
            {
                await _delay.DelayAsync(_retryDelays[retryIndex], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return AiProviderResult.Failure(AiProviderFailureKind.CANCELLED, RetryCancelledCode);
            }

            result = await _inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
