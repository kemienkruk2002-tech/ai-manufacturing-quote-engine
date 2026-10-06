using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqCanonicalReviewRepositoryTests(PostgresFixture db)
{
    private RfqCanonicalReviewRepository Repository => new(db.DataSource);
    private RfqExtractionExecutionRepository Extractions => new(db.DataSource);

    [Fact]
    public async Task Review_update_and_append_only_audit_commit_atomically()
    {
        var rfqId = await CreateRfqAsync();
        var canonical = JsonSerializer.Serialize(BaseRfq());
        var persisted = await Extractions.SaveAttemptAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "model-test",
            "rfq-extractor-prompt-v1",
            "v1",
            new string('a', 64),
            AiExecutionDisposition.COMPLETED,
            null,
            "[]",
            canonical,
            canonical));
        Assert.NotNull(persisted.CurrentDraft);

        var beforeFact = JsonSerializer.Serialize(Missing<string>());
        var result = await Repository.ApplyAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "rfq_number",
            RfqCanonicalReviewDecision.CONFIRM,
            canonical,
            beforeFact,
            beforeFact,
            1,
            "reviewer-42",
            "customer-email",
            "Checked against the source.",
            "corr-review-1"));

        Assert.Equal(RfqCanonicalReviewApplyStatus.UPDATED, result.Status);
        Assert.NotNull(result.Draft);
        Assert.Equal(2, result.Draft!.RowVersion);
        Assert.NotNull(result.ReviewEvent);
        Assert.Equal("reviewer-42", result.ReviewEvent!.Actor);
        Assert.Equal("customer-email", result.ReviewEvent.ReviewSource);
        Assert.Equal("Checked against the source.", result.ReviewEvent.Reason);
        Assert.Equal(1, result.ReviewEvent.BeforeRowVersion);
        Assert.Equal(2, result.ReviewEvent.AfterRowVersion);
        Assert.Equal("corr-review-1", result.ReviewEvent.CorrelationId);

        var history = await Repository.ListAsync(PostgresFixture.TenantId, rfqId);
        var review = Assert.Single(history);
        Assert.Equal(RfqCanonicalReviewDecision.CONFIRM, review.Decision);
        Assert.Equal(result.ReviewEvent.AuditEventId, review.AuditEventId);

        Assert.Equal(RfqCanonicalReviewRepository.ConfirmAction,
            await db.ScalarAsync<string>($"""
                SELECT action FROM audit_events WHERE id='{review.AuditEventId}'
                """));
        Assert.Equal(RfqCanonicalReviewRepository.AuditSource,
            await db.ScalarAsync<string>($"""
                SELECT source FROM audit_events WHERE id='{review.AuditEventId}'
                """));

        await db.RejectAsync($"""
            UPDATE audit_events
            SET action='CHANGED'
            WHERE id='{review.AuditEventId}'
            """, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Stale_review_version_writes_neither_draft_nor_audit()
    {
        var rfqId = await CreateRfqAsync();
        var canonical = JsonSerializer.Serialize(BaseRfq());
        await Extractions.SaveAttemptAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "model-test",
            "rfq-extractor-prompt-v1",
            "v1",
            new string('b', 64),
            AiExecutionDisposition.COMPLETED,
            null,
            "[]",
            canonical,
            canonical));

        var fact = JsonSerializer.Serialize(Missing<string>());
        var first = await Repository.ApplyAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "rfq_number",
            RfqCanonicalReviewDecision.REJECT,
            canonical,
            fact,
            fact,
            1,
            "reviewer-1",
            "manual-review",
            "Source is insufficient.",
            null));
        Assert.Equal(RfqCanonicalReviewApplyStatus.UPDATED, first.Status);

        var stale = await Repository.ApplyAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "customer_reference",
            RfqCanonicalReviewDecision.CONFIRM,
            canonical,
            fact,
            fact,
            1,
            "reviewer-2",
            "manual-review",
            "Stale review.",
            null));

        Assert.Equal(RfqCanonicalReviewApplyStatus.VERSION_CONFLICT, stale.Status);
        var history = await Repository.ListAsync(PostgresFixture.TenantId, rfqId);
        Assert.Single(history);
        var current = await Extractions.FindCurrentDraftAsync(PostgresFixture.TenantId, rfqId);
        Assert.Equal(2, current!.RowVersion);
    }

    [Fact]
    public async Task Review_history_is_tenant_scoped()
    {
        var rfqId = await CreateRfqAsync();
        var canonical = JsonSerializer.Serialize(BaseRfq());
        await Extractions.SaveAttemptAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "model-test",
            "rfq-extractor-prompt-v1",
            "v1",
            new string('c', 64),
            AiExecutionDisposition.COMPLETED,
            null,
            "[]",
            canonical,
            canonical));
        var fact = JsonSerializer.Serialize(Missing<string>());
        await Repository.ApplyAsync(new(
            PostgresFixture.TenantId,
            rfqId,
            "rfq_number",
            RfqCanonicalReviewDecision.CONFIRM,
            canonical,
            fact,
            fact,
            1,
            "reviewer-1",
            "manual-review",
            "Reviewed.",
            null));

        Assert.Single(await Repository.ListAsync(PostgresFixture.TenantId, rfqId));
        Assert.Empty(await Repository.ListAsync(Guid.NewGuid(), rfqId));
    }

    private async Task<Guid> CreateRfqAsync()
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status,currency)
            VALUES(@id,@tenant,'New','PLN')
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        await command.ExecuteNonQueryAsync();
        return id;
    }

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

    private static CanonicalRfqFact<T> Missing<T>() => new(
        [],
        default,
        [],
        null,
        RfqFactClassification.MISSING,
        false,
        []);
}
