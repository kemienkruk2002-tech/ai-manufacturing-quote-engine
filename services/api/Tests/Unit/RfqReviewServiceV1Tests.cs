using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RfqReviewServiceV1Tests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Rfq = Guid.NewGuid();

    [Fact]
    public async Task Missing_and_conflict_block_until_explicit_correction_or_confirmation()
    {
        var extraction = new ExtractionRepo(Draft());
        var reviews = new ReviewRepo();
        var service = new RfqReviewServiceV1(extraction, reviews);

        var before = await service.GetReadinessAsync(Tenant, Rfq);
        Assert.False(before.CanProgress);
        Assert.Contains(before.Blockers, x => x.FieldPath == "rfq_number" && x.Classification == RfqFactClassification.MISSING);
        Assert.Contains(before.Blockers, x => x.FieldPath == "customer_reference" && x.Classification == RfqFactClassification.CONFLICT);

        await service.ReviewAsync(new(Tenant, Rfq, 3, "rfq_number", RfqReviewAction.CORRECT,
            "\"RFQ-1\"", "user-1", "api", "Customer supplied the missing RFQ number."));
        await service.ReviewAsync(new(Tenant, Rfq, 3, "customer_reference", RfqReviewAction.CONFIRM,
            null, "user-1", "api", "Customer confirmed reference A."));

        var after = await service.GetReadinessAsync(Tenant, Rfq);
        Assert.True(after.CanProgress);
        Assert.Empty(after.Blockers);
    }

    [Fact]
    public async Task Reject_keeps_critical_fact_blocked_and_audit_fields_are_retained()
    {
        var reviews = new ReviewRepo();
        var service = new RfqReviewServiceV1(new ExtractionRepo(Draft()), reviews);
        var saved = await service.ReviewAsync(new(Tenant, Rfq, 3, "customer_reference", RfqReviewAction.REJECT,
            null, "reviewer-7", "manual-review", "Source documents still disagree."));

        Assert.NotNull(saved);
        Assert.Equal("reviewer-7", saved!.Actor);
        Assert.Equal("manual-review", saved.Source);
        Assert.Equal("Source documents still disagree.", saved.Reason);
        var readiness = await service.GetReadinessAsync(Tenant, Rfq);
        Assert.Contains(readiness.Blockers, x => x.FieldPath == "customer_reference");
    }

    [Fact]
    public async Task Stale_draft_version_returns_conflict_signal_without_writing_review()
    {
        var reviews = new ReviewRepo();
        var service = new RfqReviewServiceV1(new ExtractionRepo(Draft()), reviews);
        var saved = await service.ReviewAsync(new(Tenant, Rfq, 2, "customer_reference", RfqReviewAction.CONFIRM,
            null, "user", "api", "confirmed"));
        Assert.Null(saved);
        Assert.Empty(reviews.Items);
    }

    private static StoredRfqCanonicalDraft Draft() => new(Tenant, Rfq, Guid.NewGuid(), JsonSerializer.Serialize(
        new CanonicalRfqV1(
            Missing<string>(),
            new(["A", "B"], "A", [new("file", "a"), new("file", "b")], .5m,
                RfqFactClassification.CONFLICT, true, []),
            [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(), [], [], [], [], [])),
        3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static CanonicalRfqFact<T> Missing<T>() => new([], default, [], null, RfqFactClassification.MISSING, false, []);

    private sealed class ExtractionRepo(StoredRfqCanonicalDraft draft) : IRfqExtractionExecutionRepository
    {
        public Task<StoredRfqCanonicalDraft?> FindCurrentDraftAsync(Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredRfqCanonicalDraft?>(tenantId == Tenant && quoteRequestId == Rfq ? draft : null);
        public Task<StoredRfqExtractionExecution> SaveAsync(RfqExtractionExecutionWrite write, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RfqExtractionPersistenceResult> SaveAttemptAsync(RfqExtractionAttemptWrite write, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredRfqExtractionAttempt>> ListAttemptsAsync(Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ReviewRepo : IRfqReviewRepository
    {
        public List<StoredRfqReview> Items { get; } = [];
        public Task<StoredRfqReview?> AppendAsync(RfqReviewWrite write, CancellationToken cancellationToken = default)
        {
            var item = new StoredRfqReview(Guid.NewGuid(), write.TenantId, write.QuoteRequestId, Guid.NewGuid(),
                write.ExpectedDraftRowVersion, write.FieldPath, write.Action, write.CorrectedValueJson,
                write.Actor, write.Source, write.Reason, DateTimeOffset.UtcNow);
            Items.Add(item); return Task.FromResult<StoredRfqReview?>(item);
        }
        public Task<IReadOnlyList<StoredRfqReview>> ListAsync(Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredRfqReview>>(Items.Where(x => x.TenantId == tenantId && x.QuoteRequestId == quoteRequestId).ToArray());
    }
}
