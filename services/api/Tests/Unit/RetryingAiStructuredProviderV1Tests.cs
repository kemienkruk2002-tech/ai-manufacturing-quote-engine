using QuoteEngine.Application.Ai;

namespace QuoteEngine.UnitTests;

public sealed class RetryingAiStructuredProviderV1Tests
{
    [Fact]
    public async Task Success_first_call_returns_exact_result_without_delay()
    {
        var success = AiProviderResult.Success("{\"ok\":true}");
        var provider = Provider(success);
        var delay = new FakeDelay();

        var result = await Wrapper(provider, delay, TimeSpan.FromMilliseconds(10)).ExecuteAsync(Request());

        Assert.Same(success, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(delay.Calls);
    }

    [Theory]
    [InlineData(AiProviderFailureKind.PERMANENT, "permanent")]
    [InlineData(AiProviderFailureKind.CANCELLED, "cancelled")]
    [InlineData(AiProviderFailureKind.UNKNOWN, "unknown")]
    public async Task Non_transient_failure_is_not_retried(AiProviderFailureKind kind, string code)
    {
        var failure = AiProviderResult.Failure(kind, code);
        var provider = Provider(failure);
        var delay = new FakeDelay();

        var result = await Wrapper(provider, delay, TimeSpan.FromSeconds(1)).ExecuteAsync(Request());

        Assert.Same(failure, result);
        Assert.Equal(kind, result.FailureKind);
        Assert.Equal(code, result.FailureCode);
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(delay.Calls);
    }

    [Fact]
    public async Task Transient_then_success_uses_exact_single_retry_delay()
    {
        var success = AiProviderResult.Success("{\"ok\":true}");
        var provider = Provider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t1"),
            success);
        var delay = new FakeDelay();
        var expectedDelay = TimeSpan.FromMilliseconds(125);

        var result = await Wrapper(provider, delay, expectedDelay).ExecuteAsync(Request());

        Assert.Same(success, result);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(new[] { expectedDelay }, delay.Calls);
    }

    [Fact]
    public async Task Two_transients_then_success_preserve_retry_delay_order()
    {
        var success = AiProviderResult.Success("{\"ok\":true}");
        var provider = Provider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t1"),
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t2"),
            success);
        var delay = new FakeDelay();
        var retryDelays = new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(35) };

        var result = await Wrapper(provider, delay, retryDelays).ExecuteAsync(Request());

        Assert.Same(success, result);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(retryDelays, delay.Calls);
    }

    [Fact]
    public async Task Transient_exhaustion_returns_exact_last_failure_after_one_plus_n_calls()
    {
        var last = AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "last-transient");
        var provider = Provider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t1"),
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t2"),
            last);
        var delay = new FakeDelay();
        var retryDelays = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(1) };

        var result = await Wrapper(provider, delay, retryDelays).ExecuteAsync(Request());

        Assert.Same(last, result);
        Assert.Equal("last-transient", result.FailureCode);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(retryDelays, delay.Calls);
    }

    [Fact]
    public async Task Permanent_after_transient_stops_immediately_without_later_delay()
    {
        var permanent = AiProviderResult.Failure(AiProviderFailureKind.PERMANENT, "bad-request");
        var provider = Provider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "temporary"),
            permanent);
        var delay = new FakeDelay();
        var first = TimeSpan.FromMilliseconds(5);
        var second = TimeSpan.FromMilliseconds(50);

        var result = await Wrapper(provider, delay, first, second).ExecuteAsync(Request());

        Assert.Same(permanent, result);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(new[] { first }, delay.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_retry_delay_returns_stable_cancelled_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Provider(
            AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "temporary"),
            AiProviderResult.Success("{\"must\":\"not-run\"}"));
        var delay = new FakeDelay((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        });

        var result = await Wrapper(provider, delay, TimeSpan.FromSeconds(1))
            .ExecuteAsync(Request(), cancellation.Token);

        Assert.Equal(AiProviderResultStatus.FAILURE, result.Status);
        Assert.Equal(AiProviderFailureKind.CANCELLED, result.FailureKind);
        Assert.Equal(RetryingAiStructuredProviderV1.RetryCancelledCode, result.FailureCode);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(delay.Calls);
    }

    [Fact]
    public async Task Empty_retry_delays_allow_only_initial_transient_call()
    {
        var transient = AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "temporary");
        var provider = Provider(transient);
        var delay = new FakeDelay();

        var result = await Wrapper(provider, delay, Array.Empty<TimeSpan>()).ExecuteAsync(Request());

        Assert.Same(transient, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(delay.Calls);
    }

    [Fact]
    public void Negative_retry_delay_is_rejected_by_constructor()
    {
        var provider = Provider(AiProviderResult.Success("{}"));
        var delay = new FakeDelay();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryingAiStructuredProviderV1(provider, [TimeSpan.FromTicks(-1)], delay));
    }

    [Fact]
    public void Null_constructor_dependencies_are_rejected()
    {
        var provider = Provider(AiProviderResult.Success("{}"));
        var delay = new FakeDelay();

        Assert.Throws<ArgumentNullException>(() =>
            new RetryingAiStructuredProviderV1(null!, Array.Empty<TimeSpan>(), delay));
        Assert.Throws<ArgumentNullException>(() =>
            new RetryingAiStructuredProviderV1(provider, null!, delay));
        Assert.Throws<ArgumentNullException>(() =>
            new RetryingAiStructuredProviderV1(provider, Array.Empty<TimeSpan>(), null!));
    }

    [Fact]
    public async Task Identical_fake_sequence_one_hundred_times_has_identical_behavior()
    {
        var outcomes = new List<(int Calls, string Delays, AiProviderResultStatus Status,
            AiProviderFailureKind? Kind, string? Code)>();
        var retryDelays = new[] { TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(19) };

        for (var index = 0; index < 100; index++)
        {
            var provider = Provider(
                AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t1"),
                AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, "t2"),
                AiProviderResult.Failure(AiProviderFailureKind.PERMANENT, "final"));
            var delay = new FakeDelay();

            var result = await Wrapper(provider, delay, retryDelays).ExecuteAsync(Request());
            outcomes.Add((provider.CallCount, string.Join("|", delay.Calls.Select(value => value.Ticks)),
                result.Status, result.FailureKind, result.FailureCode));
        }

        Assert.Single(outcomes.Distinct());
        Assert.Equal(3, outcomes[0].Calls);
        Assert.Equal(AiProviderResultStatus.FAILURE, outcomes[0].Status);
        Assert.Equal(AiProviderFailureKind.PERMANENT, outcomes[0].Kind);
        Assert.Equal("final", outcomes[0].Code);
    }

    private static RetryingAiStructuredProviderV1 Wrapper(FakeProvider provider, FakeDelay delay,
        params TimeSpan[] retryDelays) => new(provider, retryDelays, delay);

    private static RetryingAiStructuredProviderV1 Wrapper(FakeProvider provider, FakeDelay delay,
        IReadOnlyList<TimeSpan> retryDelays) => new(provider, retryDelays, delay);

    private static FakeProvider Provider(params AiProviderResult[] results) => new(results);

    private static AiStructuredRequest Request() => new("rfq-extractor", "model-a", "prompt-v1", "v1", "{}");

    private sealed class FakeProvider(IEnumerable<AiProviderResult> results) : IAiStructuredProvider
    {
        private readonly Queue<AiProviderResult> _results = new(results);

        public int CallCount { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_results.Count == 0)
                throw new InvalidOperationException("Fake provider received more calls than expected.");
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeDelay(Action<TimeSpan, CancellationToken>? action = null) : IAiRetryDelay
    {
        public List<TimeSpan> Calls { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls.Add(delay);
            action?.Invoke(delay, cancellationToken);
            return Task.CompletedTask;
        }
    }
}
