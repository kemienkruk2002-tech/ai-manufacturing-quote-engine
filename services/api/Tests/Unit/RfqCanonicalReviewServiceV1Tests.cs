using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RfqCanonicalReviewServiceV1Tests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
    private static readonly Guid RfqId = Guid.Parse("90000000-0000-0000-0000-000000000201");
    private static readonly Guid AttemptId = Guid.Parse("80000000-0000-0000-0000-000000000201");

    [Fact]
    public async Task Correct_replaces_exact_fact_and_revalidates_whole_canonical_rfq()
    {
        var extraction = new FakeExtractionRepository(Draft(BaseRfq(), 4));
        var reviews = new FakeReviewRepository(extraction);
        var service = new RfqCanonicalReviewServiceV1(extraction, reviews);
        var corrected = JsonSerializer.SerializeToElement(TextFact("RFQ-2026-001"));

        var result = await service.ReviewAsync(new(
            TenantId,
            RfqId,
            "rfq_number",
            RfqCanonicalReviewDecision.CORRECT,
            corrected,
            4,
            "reviewer-1",
            "customer-email",
            "Confirmed against customer email.",
            "corr-1"));

        Assert.Equal(RfqCanonicalReviewApplyStatus.UPDATED, result.Status);
        var write = Assert.Single(reviews.Writes);
        Assert.Equal("reviewer-1", write.Actor);
        Assert.Equal("customer-email", write.ReviewSource);
        Assert.Equal("Confirmed against customer email.", write.Reason);
        Assert.Equal(4, write.ExpectedRowVersion);

        var guard = RfqExtractorOutputGuardV1.Evaluate(write.CanonicalJson);
        Assert.True(guard.IsPass);
        Assert.Equal("RFQ-2026-001", guard.Output!.RfqNumber.NormalizedValue);
        Assert.Equal(RfqFactClassification.EXPLICIT, guard.Output.RfqNumber.Classification);
    }

    [Fact]
    public async Task Correct_rejects_replacement_that_breaks_existing_v1_contract()
    {
        var extraction = new FakeExtractionRepository(Draft(BaseRfq(), 1));
        var reviews = new FakeReviewRepository(extraction);
        var service = new RfqCanonicalReviewServiceV1(extraction, reviews);
        var invalid = JsonSerializer.SerializeToElement(new { normalized_value = "invented" });

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.ReviewAsync(new(
                TenantId,
                RfqId,
                "rfq_number",
                RfqCanonicalReviewDecision.CORRECT,
                invalid,
                1,
                "reviewer-1",
                "manual-review",
                "Replacement supplied.",
                null)));

        Assert.Contains(error.Errors,
            issue => issue.Code == RfqCanonicalReviewServiceV1.CorrectionInvalidCode);
        Assert.Empty(reviews.Writes);
    }

    [Fact]
    public async Task Review_targets_whole_fact_only_not_nested_subproperties()
    {
        var extraction = new FakeExtractionRepository(Draft(BaseRfq(), 1));
        var reviews = new FakeReviewRepository(extraction);
        var service = new RfqCanonicalReviewServiceV1(extraction, reviews);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.ReviewAsync(new(
                TenantId,
                RfqId,
                "rfq_number.normalized_value",
                RfqCanonicalReviewDecision.CONFIRM,
                null,
                1,
                "reviewer-1",
                "manual-review",
                "Reviewed.",
                null)));

        Assert.Contains(error.Errors,
            issue => issue.Code == RfqCanonicalReviewServiceV1.FieldPathInvalidCode);
        Assert.Empty(reviews.Writes);
    }

    [Fact]
    public async Task Readiness_keeps_missing_conflict_and_latest_reject_as_explicit_blockers()
    {
        var rfq = ResolvedRfq() with
        {
            Revisions =
            [
                new(
                    ["A", "B"],
                    "A",
                    [Source("pdf", "drawing"), Source("mail", "message")],
                    0.5m,
                    RfqFactClassification.CONFLICT,
                    true,
                    ["sources disagree"])
            ],
            OpenQuestions = [Missing<string>()]
        };
        var extraction = new FakeExtractionRepository(Draft(rfq, 7));
        var reviews = new FakeReviewRepository(extraction);
        reviews.Events.Add(Event(
            "rfq_number",
            RfqCanonicalReviewDecision.REJECT,
            before: 5,
            after: 6));
        reviews.Events.Add(Event(
            "revisions[0]",
            RfqCanonicalReviewDecision.CONFIRM,
            before: 6,
            after: 7));

        var readiness = await new RfqCanonicalReviewServiceV1(extraction, reviews)
            .GetReadinessAsync(TenantId, RfqId);

        Assert.NotNull(readiness);
        Assert.False(readiness!.IsReviewComplete);
        Assert.Contains(readiness.Blockers,
            blocker => blocker.FieldPath == "rfq_number"
                && blocker.Code == RfqCanonicalReviewServiceV1.RejectedBlockerCode);
        Assert.Contains(readiness.Blockers,
            blocker => blocker.FieldPath == "revisions[0]"
                && blocker.Code == RfqCanonicalReviewServiceV1.ConflictBlockerCode);
        Assert.Contains(readiness.Blockers,
            blocker => blocker.FieldPath == "open_questions[0]"
                && blocker.Code == RfqCanonicalReviewServiceV1.MissingBlockerCode);
    }

    private static StoredRfqCanonicalDraft Draft(CanonicalRfqV1 rfq, long version) =>
        new(
            TenantId,
            RfqId,
            AttemptId,
            JsonSerializer.Serialize(rfq),
            version,
            DateTimeOffset.Parse("2026-10-06T08:00:00Z"),
            DateTimeOffset.Parse("2026-10-06T08:00:00Z"));

    private static StoredRfqCanonicalReviewEvent Event(
        string path,
        RfqCanonicalReviewDecision decision,
        long before,
        long after) =>
        new(
            Guid.NewGuid(),
            TenantId,
            RfqId,
            AttemptId,
            path,
            decision,
            "reviewer-1",
            "manual-review",
            "Reviewed.",
            "{}",
            "{}",
            before,
            after,
            null,
            DateTimeOffset.Parse("2026-10-06T08:00:00Z").AddSeconds(after));

    private static CanonicalRfqV1 BaseRfq() => new(
        Missing<string>(),
        Missing<string>(),
        [],
        [],
        [],
        Missing<DateOnly?>(),
        Missing<DateOnly?>(),
        [],
        [],
        [],
        [],
        []);

    private static CanonicalRfqV1 ResolvedRfq() => new(
        TextFact("RFQ-1"),
        TextFact("CUSTOMER-1"),
        [],
        [],
        [],
        DateFact(new DateOnly(2026, 10, 20)),
        DateFact(new DateOnly(2026, 11, 20)),
        [],
        [],
        [],
        [],
        []);

    private static CanonicalRfqFact<string> TextFact(string value) => new(
        [value],
        value,
        [Source("rfq_file", "source-1")],
        0.99m,
        RfqFactClassification.EXPLICIT,
        false,
        []);

    private static CanonicalRfqFact<DateOnly?> DateFact(DateOnly value) => new(
        [value.ToString("yyyy-MM-dd")],
        value,
        [Source("rfq_file", "source-1")],
        0.99m,
        RfqFactClassification.EXPLICIT,
        false,
        []);

    private static CanonicalRfqFact<T> Missing<T>() => new(
        [],
        default,
        [],
        null,
        RfqFactClassification.MISSING,
        false,
        []);

    private static RfqSourceReference Source(string type, string id) => new(type, id);

    private sealed class FakeExtractionRepository(StoredRfqCanonicalDraft? current)
        : IRfqExtractionExecutionRepository
    {
        public StoredRfqCanonicalDraft? Current { get; set; } = current;

        public Task<StoredRfqCanonicalDraft?> FindCurrentDraftAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                tenantId == TenantId && quoteRequestId == RfqId ? Current : null);

        public Task<RfqExtractionAtomicPersistenceResult> SaveAtomicAsync(
            RfqExtractionExecutionWrite execution,
            RfqExtractionAttemptWrite attempt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRfqExtractionExecution> SaveAsync(
            RfqExtractionExecutionWrite write,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RfqExtractionPersistenceResult> SaveAttemptAsync(
            RfqExtractionAttemptWrite write,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredRfqExtractionAttempt>> ListAttemptsAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReviewRepository(FakeExtractionRepository extraction)
        : IRfqCanonicalReviewRepository
    {
        public List<RfqCanonicalReviewWrite> Writes { get; } = [];
        public List<StoredRfqCanonicalReviewEvent> Events { get; } = [];

        public Task<RfqCanonicalReviewApplyResult> ApplyAsync(
            RfqCanonicalReviewWrite write,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(write);
            var current = extraction.Current;
            if (current is null)
                return Task.FromResult(new RfqCanonicalReviewApplyResult(
                    RfqCanonicalReviewApplyStatus.DRAFT_NOT_FOUND, null, null));
            if (current.RowVersion != write.ExpectedRowVersion)
                return Task.FromResult(new RfqCanonicalReviewApplyResult(
                    RfqCanonicalReviewApplyStatus.VERSION_CONFLICT, null, null));

            var updated = current with
            {
                CanonicalJson = write.CanonicalJson,
                RowVersion = current.RowVersion + 1,
                UpdatedAt = current.UpdatedAt.AddSeconds(1)
            };
            extraction.Current = updated;
            var review = new StoredRfqCanonicalReviewEvent(
                Guid.NewGuid(),
                write.TenantId,
                write.QuoteRequestId,
                updated.SourceAttemptId,
                write.FieldPath,
                write.Decision,
                write.Actor,
                write.ReviewSource,
                write.Reason,
                write.BeforeFactJson,
                write.AfterFactJson,
                write.ExpectedRowVersion,
                updated.RowVersion,
                write.CorrelationId,
                updated.UpdatedAt);
            Events.Add(review);
            return Task.FromResult(new RfqCanonicalReviewApplyResult(
                RfqCanonicalReviewApplyStatus.UPDATED, updated, review));
        }

        public Task<IReadOnlyList<StoredRfqCanonicalReviewEvent>> ListAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredRfqCanonicalReviewEvent>>(
                Events.Where(item => item.TenantId == tenantId && item.QuoteRequestId == quoteRequestId)
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.AuditEventId)
                    .ToArray());
    }
}
