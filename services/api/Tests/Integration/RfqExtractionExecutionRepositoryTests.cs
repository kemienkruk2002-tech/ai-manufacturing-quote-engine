using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqExtractionExecutionRepositoryTests(PostgresFixture db)
{
    private RfqExtractionExecutionRepository Repository => new(db.DataSource);

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
}
