using Npgsql;
using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqExtractionExecutionRepositoryTests(PostgresFixture db)
{
    private RfqExtractionExecutionRepository Repository => new(db.DataSource);

    [Fact]
    public async Task Atomic_save_commits_one_audit_one_attempt_and_validated_current_draft()
    {
        var rfqId = await CreateRfqAsync();
        var fingerprint = new string('d', 64);
        var canonical = ValidCanonicalJson();

        var result = await Repository.SaveAtomicAsync(
            new(
                PostgresFixture.TenantId,
                rfqId,
                "model-test",
                RfqExtractorPromptV1.PromptVersion,
                RfqExtractorPromptV1.SchemaVersion,
                fingerprint,
                AiExecutionDisposition.COMPLETED,
                null),
            new(
                PostgresFixture.TenantId,
                rfqId,
                "model-test",
                RfqExtractorPromptV1.PromptVersion,
                RfqExtractorPromptV1.SchemaVersion,
                fingerprint,
                AiExecutionDisposition.COMPLETED,
                null,
                "[]",
                canonical,
                canonical));

        Assert.Equal(1L, await CountExtractionAuditsAsync(rfqId));
        Assert.Equal(1L, await CountAttemptsAsync(rfqId));
        Assert.NotNull(result.Persistence.CurrentDraft);
        Assert.Equal(result.Persistence.Attempt.Id, result.Persistence.CurrentDraft!.SourceAttemptId);
    }

    [Fact]
    public async Task Atomic_save_rolls_back_audit_and_attempt_when_draft_write_fails()
    {
        var rfqId = await CreateRfqAsync();
        var canonical = ValidCanonicalJson();

        await using (var command = db.DataSource.CreateCommand("""
            ALTER TABLE rfq_canonical_drafts
            ADD CONSTRAINT b3_h1_forced_draft_failure CHECK (false) NOT VALID
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Repository.SaveAtomicAsync(
                    new(
                        PostgresFixture.TenantId,
                        rfqId,
                        "model-test",
                        RfqExtractorPromptV1.PromptVersion,
                        RfqExtractorPromptV1.SchemaVersion,
                        new string('e', 64),
                        AiExecutionDisposition.COMPLETED,
                        null),
                    new(
                        PostgresFixture.TenantId,
                        rfqId,
                        "model-test",
                        RfqExtractorPromptV1.PromptVersion,
                        RfqExtractorPromptV1.SchemaVersion,
                        new string('e', 64),
                        AiExecutionDisposition.COMPLETED,
                        null,
                        "[]",
                        canonical,
                        canonical)));

            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal(0L, await CountExtractionAuditsAsync(rfqId));
            Assert.Equal(0L, await CountAttemptsAsync(rfqId));
        }
        finally
        {
            await using var cleanup = db.DataSource.CreateCommand("""
                ALTER TABLE rfq_canonical_drafts
                DROP CONSTRAINT IF EXISTS b3_h1_forced_draft_failure
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Atomic_save_rejects_mismatched_execution_and_attempt_before_writing()
    {
        var rfqId = await CreateRfqAsync();

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Repository.SaveAtomicAsync(
                new(
                    PostgresFixture.TenantId,
                    rfqId,
                    "model-a",
                    RfqExtractorPromptV1.PromptVersion,
                    RfqExtractorPromptV1.SchemaVersion,
                    new string('f', 64),
                    AiExecutionDisposition.REVIEW_MANUAL,
                    "TEST"),
                new(
                    PostgresFixture.TenantId,
                    rfqId,
                    "model-b",
                    RfqExtractorPromptV1.PromptVersion,
                    RfqExtractorPromptV1.SchemaVersion,
                    new string('f', 64),
                    AiExecutionDisposition.REVIEW_MANUAL,
                    "TEST",
                    "[]",
                    null,
                    null)));

        Assert.Contains(error.Errors,
            issue => issue.Code == "RFQ_EXTRACTION_ATOMIC_WRITE_MISMATCH");
        Assert.Equal(0L, await CountExtractionAuditsAsync(rfqId));
        Assert.Equal(0L, await CountAttemptsAsync(rfqId));
    }

    [Fact]
    public async Task Save_appends_required_execution_metadata_to_immutable_audit()
    {
        var fingerprint = new string('a', 64);

        var stored = await Repository.SaveAsync(new(
            PostgresFixture.TenantId,
            PostgresFixture.RequestId,
            "model-test",
            RfqExtractorPromptV1.PromptVersion,
            RfqExtractorPromptV1.SchemaVersion,
            fingerprint,
            AiExecutionDisposition.REVIEW_MANUAL,
            "AI_EXTERNAL_NOT_ALLOWED"));

        Assert.NotEqual(Guid.Empty, stored.AuditEventId);
        Assert.Equal(PostgresFixture.TenantId, stored.TenantId);
        Assert.Equal(PostgresFixture.RequestId, stored.QuoteRequestId);
        Assert.Equal(fingerprint, stored.RequestFingerprint);

        var json = await db.ScalarAsync<string>($"""
            SELECT new_value::text
            FROM audit_events
            WHERE id='{stored.AuditEventId}'
            """);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(fingerprint, root.GetProperty("request_fingerprint").GetString());
        Assert.Equal("model-test", root.GetProperty("model_id").GetString());
        Assert.Equal(RfqExtractorPromptV1.PromptVersion,
            root.GetProperty("prompt_version").GetString());
        Assert.Equal(RfqExtractorPromptV1.SchemaVersion,
            root.GetProperty("schema_version").GetString());
        Assert.Equal("REVIEW_MANUAL", root.GetProperty("disposition").GetString());
        Assert.Equal("AI_EXTERNAL_NOT_ALLOWED", root.GetProperty("code").GetString());

        Assert.Equal(RfqExtractionExecutionRepository.Action,
            await db.ScalarAsync<string>($"SELECT action FROM audit_events WHERE id='{stored.AuditEventId}'"));
        Assert.Equal(RfqExtractionExecutionRepository.Source,
            await db.ScalarAsync<string>($"SELECT source FROM audit_events WHERE id='{stored.AuditEventId}'"));
    }

    [Fact]
    public async Task Review_before_ai_request_can_persist_null_fingerprint()
    {
        var stored = await Repository.SaveAsync(new(
            PostgresFixture.TenantId,
            PostgresFixture.RequestId,
            "model-test",
            RfqExtractorPromptV1.PromptVersion,
            RfqExtractorPromptV1.SchemaVersion,
            null,
            AiExecutionDisposition.REVIEW_MANUAL,
            RfqExtractionServiceV1.SourceRequiredCode));

        var json = await db.ScalarAsync<string>($"""
            SELECT new_value::text
            FROM audit_events
            WHERE id='{stored.AuditEventId}'
            """);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null,
            document.RootElement.GetProperty("request_fingerprint").ValueKind);
    }

    [Fact]
    public async Task Cross_tenant_or_unknown_rfq_cannot_receive_extraction_audit()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Repository.SaveAsync(new(
                Guid.NewGuid(),
                PostgresFixture.RequestId,
                "model-test",
                RfqExtractorPromptV1.PromptVersion,
                RfqExtractorPromptV1.SchemaVersion,
                new string('b', 64),
                AiExecutionDisposition.REVIEW_MANUAL,
                "TEST")));

        Assert.Contains(error.Errors,
            issue => issue.Code == "RFQ_EXTRACTION_AUDIT_RFQ_NOT_FOUND");
    }

    [Fact]
    public async Task Extraction_audit_event_remains_append_only()
    {
        var stored = await Repository.SaveAsync(new(
            PostgresFixture.TenantId,
            PostgresFixture.RequestId,
            "model-test",
            RfqExtractorPromptV1.PromptVersion,
            RfqExtractorPromptV1.SchemaVersion,
            new string('c', 64),
            AiExecutionDisposition.REVIEW_MANUAL,
            "TEST"));

        await db.RejectAsync($"""
            UPDATE audit_events
            SET action='CHANGED'
            WHERE id='{stored.AuditEventId}'
            """, "23514");
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

    private async Task<long> CountExtractionAuditsAsync(Guid rfqId) =>
        await db.ScalarAsync<long>($"""
            SELECT count(*)
            FROM audit_events
            WHERE tenant_id='{PostgresFixture.TenantId}'
              AND entity_id='{rfqId}'
              AND action='{RfqExtractionExecutionRepository.Action}'
            """);

    private async Task<long> CountAttemptsAsync(Guid rfqId) =>
        await db.ScalarAsync<long>($"""
            SELECT count(*)
            FROM rfq_extraction_attempts
            WHERE tenant_id='{PostgresFixture.TenantId}'
              AND quote_request_id='{rfqId}'
            """);

    private static string ValidCanonicalJson() =>
        JsonSerializer.Serialize(new CanonicalRfqV1(
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
            []));

    private static CanonicalRfqFact<T> Missing<T>() =>
        new([], default, [], null, RfqFactClassification.MISSING, false, []);

}
