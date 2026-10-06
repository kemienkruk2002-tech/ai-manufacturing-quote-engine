using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqExtractionHistoryRepositoryTests(PostgresFixture db)
{
    private RfqExtractionExecutionRepository Repository => new(db.DataSource);

    [Fact]
    public async Task Completed_attempt_retains_raw_output_lineage_and_initializes_separate_canonical_draft()
    {
        var rfqId = await CreateRfqAsync();
        var raw = CanonicalWithMissingAndConflict(pretty: true);
        var lineage = """
            [
              {
                "logical_key": "drawing",
                "version_no": 2,
                "document_type": "application/pdf",
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "source_reference": "mail:42"
              }
            ]
            """;

        var saved = await Repository.SaveAttemptAsync(Write(
            rfqId,
            AiExecutionDisposition.COMPLETED,
            lineage,
            rawProviderOutput: raw,
            canonicalDraftJson: raw));

        Assert.NotEqual(Guid.Empty, saved.Attempt.Id);
        Assert.Equal(raw, saved.Attempt.RawProviderOutput);
        Assert.NotNull(saved.CurrentDraft);
        Assert.Equal(saved.Attempt.Id, saved.CurrentDraft!.SourceAttemptId);
        Assert.Equal(1, saved.CurrentDraft.RowVersion);

        var attempts = await Repository.ListAttemptsAsync(PostgresFixture.TenantId, rfqId);
        var attempt = Assert.Single(attempts);
        Assert.Equal(raw, attempt.RawProviderOutput);

        using (var lineageJson = JsonDocument.Parse(attempt.SourceLineageJson))
        {
            var source = Assert.Single(lineageJson.RootElement.EnumerateArray().ToArray());
            Assert.Equal("drawing", source.GetProperty("logical_key").GetString());
            Assert.Equal(2, source.GetProperty("version_no").GetInt32());
            Assert.Equal("application/pdf", source.GetProperty("document_type").GetString());
            Assert.Equal("mail:42", source.GetProperty("source_reference").GetString());
        }

        var draft = await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfqId);
        Assert.NotNull(draft);
        var guard = RfqExtractorOutputGuardV1.Evaluate(draft!.CanonicalJson);
        Assert.True(guard.IsPass);
        Assert.Equal(RfqFactClassification.MISSING, guard.Output!.RfqNumber.Classification);
        Assert.Equal(RfqFactClassification.CONFLICT, guard.Output.CustomerReference.Classification);
        Assert.True(guard.Output.CustomerReference.RequiresConfirmation);
    }

    [Fact]
    public async Task Second_completed_attempt_replaces_only_current_draft_and_increments_row_version()
    {
        var rfqId = await CreateRfqAsync();
        var firstRaw = CanonicalWithExplicitRfq("RFQ-FIRST");
        var secondRaw = CanonicalWithExplicitRfq("RFQ-SECOND");

        var first = await Repository.SaveAttemptAsync(Write(
            rfqId, AiExecutionDisposition.COMPLETED, "[]", firstRaw, firstRaw));
        var second = await Repository.SaveAttemptAsync(Write(
            rfqId, AiExecutionDisposition.COMPLETED, "[]", secondRaw, secondRaw));

        Assert.NotNull(first.CurrentDraft);
        Assert.NotNull(second.CurrentDraft);
        Assert.Equal(1, first.CurrentDraft!.RowVersion);
        Assert.Equal(2, second.CurrentDraft!.RowVersion);
        Assert.Equal(second.Attempt.Id, second.CurrentDraft.SourceAttemptId);

        var attempts = await Repository.ListAttemptsAsync(PostgresFixture.TenantId, rfqId);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(first.Attempt.Id, attempts[0].Id);
        Assert.Equal(second.Attempt.Id, attempts[1].Id);
        Assert.Equal(firstRaw, attempts[0].RawProviderOutput);
        Assert.Equal(secondRaw, attempts[1].RawProviderOutput);

        var current = await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfqId);
        Assert.Equal(second.Attempt.Id, current!.SourceAttemptId);
        var guard = RfqExtractorOutputGuardV1.Evaluate(current.CanonicalJson);
        Assert.True(guard.IsPass);
        Assert.Equal("RFQ-SECOND", guard.Output!.RfqNumber.NormalizedValue);
    }

    [Fact]
    public async Task Review_manual_attempt_is_retained_but_does_not_replace_current_draft()
    {
        var rfqId = await CreateRfqAsync();
        var accepted = CanonicalWithExplicitRfq("RFQ-ACCEPTED");
        var completed = await Repository.SaveAttemptAsync(Write(
            rfqId, AiExecutionDisposition.COMPLETED, "[]", accepted, accepted));
        var before = await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfqId);

        const string rejectedRaw = """{"schema":"RFQ_EXTRACTOR","version":"v1","unexpected":true}""";
        var review = await Repository.SaveAttemptAsync(Write(
            rfqId,
            AiExecutionDisposition.REVIEW_MANUAL,
            "[]",
            rejectedRaw,
            canonicalDraftJson: null,
            code: RfqExtractorOutputGuardV1.ContractInvalidCode));

        Assert.Null(review.CurrentDraft);
        var after = await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfqId);
        Assert.Equal(before!.SourceAttemptId, after!.SourceAttemptId);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(completed.Attempt.Id, after.SourceAttemptId);

        var attempts = await Repository.ListAttemptsAsync(PostgresFixture.TenantId, rfqId);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(rejectedRaw, attempts[1].RawProviderOutput);
        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, attempts[1].Disposition);
        Assert.Equal(RfqExtractorOutputGuardV1.ContractInvalidCode, attempts[1].Code);
    }

    [Fact]
    public async Task Repository_rejects_noncanonical_object_as_current_canonical_rfq_draft()
    {
        var rfqId = await CreateRfqAsync();
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Repository.SaveAttemptAsync(Write(
                rfqId,
                AiExecutionDisposition.COMPLETED,
                "[]",
                rawProviderOutput: "{}",
                canonicalDraftJson: "{}")));

        Assert.Contains(error.Errors, issue => issue.Code == "RFQ_EXTRACTION_DRAFT_INVALID");
        Assert.Empty(await Repository.ListAttemptsAsync(PostgresFixture.TenantId, rfqId));
    }

    [Fact]
    public async Task Extraction_attempt_history_is_immutable_at_database_boundary()
    {
        var rfqId = await CreateRfqAsync();
        var saved = await Repository.SaveAttemptAsync(Write(
            rfqId,
            AiExecutionDisposition.REVIEW_MANUAL,
            "[]",
            rawProviderOutput: "provider-output",
            canonicalDraftJson: null,
            code: "TEST_REVIEW"));

        await db.RejectAsync($"""
            UPDATE rfq_extraction_attempts
            SET result_code='CHANGED'
            WHERE id='{saved.Attempt.Id}'
            """, PostgresErrorCodes.CheckViolation);

        await db.RejectAsync($"""
            DELETE FROM rfq_extraction_attempts
            WHERE id='{saved.Attempt.Id}'
            """, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Current_draft_cannot_reference_attempt_from_another_rfq_in_same_tenant()
    {
        var firstRfq = await CreateRfqAsync();
        var secondRfq = await CreateRfqAsync();
        var attempt = await Repository.SaveAttemptAsync(Write(
            firstRfq,
            AiExecutionDisposition.REVIEW_MANUAL,
            "[]",
            rawProviderOutput: "provider-output",
            canonicalDraftJson: null,
            code: "TEST_REVIEW"));
        var canonical = CanonicalWithExplicitRfq("RFQ-OTHER");

        await db.RejectAsync($"""
            INSERT INTO rfq_canonical_drafts(
                tenant_id,quote_request_id,source_attempt_id,canonical_json,row_version)
            VALUES(
                '{PostgresFixture.TenantId}',
                '{secondRfq}',
                '{attempt.Attempt.Id}',
                '{EscapeSqlLiteral(canonical)}'::jsonb,
                1)
            """, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Attempt_history_is_tenant_and_rfq_scoped()
    {
        var rfqId = await CreateRfqAsync();
        await Repository.SaveAttemptAsync(Write(
            rfqId,
            AiExecutionDisposition.REVIEW_MANUAL,
            "[]",
            rawProviderOutput: "provider-output",
            canonicalDraftJson: null,
            code: "TEST_REVIEW"));

        Assert.Single(await Repository.ListAttemptsAsync(PostgresFixture.TenantId, rfqId));
        Assert.Empty(await Repository.ListAttemptsAsync(Guid.NewGuid(), rfqId));
        Assert.Null(await Repository.FindCurrentDraftAsync(Guid.NewGuid(), rfqId));
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

    private static RfqExtractionAttemptWrite Write(
        Guid rfqId,
        AiExecutionDisposition disposition,
        string lineage,
        string? rawProviderOutput,
        string? canonicalDraftJson,
        string? code = null) =>
        new(
            PostgresFixture.TenantId,
            rfqId,
            "model-test",
            RfqExtractorPromptV1.PromptVersion,
            RfqExtractorPromptV1.SchemaVersion,
            new string('d', 64),
            disposition,
            code,
            lineage,
            rawProviderOutput,
            canonicalDraftJson);

    private static string CanonicalWithMissingAndConflict(bool pretty)
    {
        var value = new CanonicalRfqV1(
            Missing<string>(),
            new(
                ["CUSTOMER-A", "CUSTOMER-B"],
                null,
                [new("rfq_file", "drawing:v1"), new("rfq_file", "email:v1")],
                0.6m,
                RfqFactClassification.CONFLICT,
                true,
                ["sources disagree"]),
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
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = pretty });
    }

    private static string CanonicalWithExplicitRfq(string value) =>
        JsonSerializer.Serialize(new CanonicalRfqV1(
            new(
                [value],
                value,
                [new("rfq_file", "drawing:v1")],
                0.99m,
                RfqFactClassification.EXPLICIT,
                false,
                []),
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

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
