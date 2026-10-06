using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqCanonicalReviewApiTests(PostgresFixture db)
{
    [Fact]
    public async Task Authenticated_review_endpoint_records_actor_source_reason_and_advances_version()
    {
        var rfqId = await CreateDraftWithCanonicalAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PostAsJsonAsync(
            ReviewUrl(rfqId),
            new
            {
                fieldPath = "rfq_number",
                decision = "CONFIRM",
                correctedFact = (object?)null,
                expectedRowVersion = 1,
                source = "customer-email",
                reason = "Checked against source."
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UPDATED", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("draft").GetProperty("rowVersion").GetInt64());
        var review = body.GetProperty("reviewEvent");
        Assert.Equal("test-user", review.GetProperty("actor").GetString());
        Assert.Equal("customer-email", review.GetProperty("reviewSource").GetString());
        Assert.Equal("Checked against source.", review.GetProperty("reason").GetString());

        using var historyResponse = await api.Client.GetAsync(HistoryUrl(rfqId));
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal("test-user", history[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Stale_review_returns_conflict_and_does_not_append_second_event()
    {
        var rfqId = await CreateDraftWithCanonicalAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var first = await api.Client.PostAsJsonAsync(
            ReviewUrl(rfqId),
            ReviewBody(1));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var stale = await api.Client.PostAsJsonAsync(
            ReviewUrl(rfqId),
            ReviewBody(1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RFQ_CANONICAL_REVIEW_VERSION_CONFLICT",
            problem.GetProperty("code").GetString());

        using var historyResponse = await api.Client.GetAsync(HistoryUrl(rfqId));
        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, history.GetArrayLength());
    }

    [Fact]
    public async Task Readiness_reports_unresolved_missing_without_changing_rfq_status()
    {
        var rfqId = await CreateDraftWithCanonicalAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.GetAsync(ReadinessUrl(rfqId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isReviewComplete").GetBoolean());
        Assert.Contains(
            body.GetProperty("blockers").EnumerateArray(),
            item => item.GetProperty("fieldPath").GetString() == "rfq_number"
                && item.GetProperty("code").GetString()
                    == RfqCanonicalReviewServiceV1.MissingBlockerCode);

        Assert.Equal("New", await db.ScalarAsync<string>($"""
            SELECT status FROM quote_requests WHERE id='{rfqId}'
            """));
    }

    [Fact]
    public async Task Correction_endpoint_replaces_fact_but_rejects_contract_bypass()
    {
        var rfqId = await CreateDraftWithCanonicalAsync();
        using var api = new RfqFileApiContext(db.ConnectionString);
        var corrected = JsonSerializer.SerializeToElement(new CanonicalRfqFact<string>(
            ["RFQ-API-1"],
            "RFQ-API-1",
            [new("customer_email", "message-1")],
            null,
            RfqFactClassification.EXPLICIT,
            false,
            []));

        using var ok = await api.Client.PostAsJsonAsync(
            ReviewUrl(rfqId),
            new
            {
                fieldPath = "rfq_number",
                decision = "CORRECT",
                correctedFact = corrected,
                expectedRowVersion = 1,
                source = "customer-email",
                reason = "Customer supplied corrected RFQ number."
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var draftResponse = await api.Client.GetAsync(DraftUrl(rfqId));
        var draft = await draftResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var canonical = JsonDocument.Parse(
            draft.GetProperty("canonicalJson").GetString()!);
        Assert.Equal("RFQ-API-1",
            canonical.RootElement.GetProperty("rfq_number")
                .GetProperty("normalized_value").GetString());

        using var invalid = await api.Client.PostAsJsonAsync(
            ReviewUrl(rfqId),
            new
            {
                fieldPath = "customer_reference",
                decision = "CORRECT",
                correctedFact = new { normalized_value = "bypass" },
                expectedRowVersion = 2,
                source = "manual-review",
                reason = "Invalid replacement."
            });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DOMAIN_VALIDATION_FAILED", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString()
                == RfqCanonicalReviewServiceV1.CorrectionInvalidCode);
    }

    private static object ReviewBody(long expectedRowVersion) => new
    {
        fieldPath = "rfq_number",
        decision = "CONFIRM",
        correctedFact = (object?)null,
        expectedRowVersion,
        source = "manual-review",
        reason = "Reviewed."
    };

    private async Task<Guid> CreateDraftWithCanonicalAsync()
    {
        var id = Guid.NewGuid();
        await using (var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status,currency)
            VALUES(@id,@tenant,'New','PLN')
            """))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await command.ExecuteNonQueryAsync();
        }

        var canonical = JsonSerializer.Serialize(BaseRfq());
        await new RfqExtractionExecutionRepository(db.DataSource).SaveAttemptAsync(new(
            PostgresFixture.TenantId,
            id,
            "model-test",
            "rfq-extractor-prompt-v1",
            "v1",
            new string('d', 64),
            AiExecutionDisposition.COMPLETED,
            null,
            "[]",
            canonical,
            canonical));
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

    private static string DraftUrl(Guid rfqId) =>
        $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{rfqId}/canonical-draft";

    private static string ReviewUrl(Guid rfqId) =>
        $"/api/tenants/{PostgresFixture.TenantId}/rfqs/{rfqId}/canonical-review";

    private static string ReadinessUrl(Guid rfqId) =>
        ReviewUrl(rfqId) + "/readiness";

    private static string HistoryUrl(Guid rfqId) =>
        ReviewUrl(rfqId) + "/history";
}
