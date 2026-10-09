using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqInputValidationApiTests(PostgresFixture db)
{
    private string CreateUrl => $"/api/tenants/{PostgresFixture.TenantId}/rfqs";
    private string UpdateUrl(Guid id) => $"{CreateUrl}/{id}/draft";

    [Theory]
    [InlineData("pln", 1, "RFQ_CURRENCY_INVALID")]
    [InlineData("PL", 1, "RFQ_CURRENCY_INVALID")]
    [InlineData("P1N", 1, "RFQ_CURRENCY_INVALID")]
    [InlineData("", 1, "RFQ_CURRENCY_INVALID")]
    [InlineData("PLN", 0, "RFQ_QUANTITY_INVALID")]
    [InlineData("PLN", -4, "RFQ_QUANTITY_INVALID")]
    public async Task Create_rejects_invalid_fields_without_inserting(
        string currency, int quantity, string expectedError)
    {
        var id = Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var response = await api.Client.PostAsJsonAsync(CreateUrl,
            new { id, currency, requestedQuantity = quantity });

        await AssertValidationProblemAsync(response, expectedError);
        Assert.Equal(0L, await StoredCountAsync(id));
    }

    [Theory]
    [InlineData("rfq")]
    [InlineData("customer")]
    [InlineData("part")]
    public async Task Create_rejects_empty_identifiers_without_inserting(string field)
    {
        var id = field == "rfq" ? Guid.Empty : Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var response = await api.Client.PostAsJsonAsync(CreateUrl, new
        {
            id,
            customerId = field == "customer" ? Guid.Empty : (Guid?)null,
            partRevisionId = field == "part" ? Guid.Empty : (Guid?)null
        });

        await AssertValidationProblemAsync(response,
            field == "rfq" ? "RFQ_IDENTIFIER_INVALID" : "RFQ_REFERENCE_ID_INVALID");
        Assert.Equal(0L, await StoredCountAsync(id));
    }

    [Theory]
    [InlineData("pln", 7, "RFQ_CURRENCY_INVALID")]
    [InlineData("PL", 7, "RFQ_CURRENCY_INVALID")]
    [InlineData("P1N", 7, "RFQ_CURRENCY_INVALID")]
    [InlineData("", 7, "RFQ_CURRENCY_INVALID")]
    [InlineData("PLN", 0, "RFQ_QUANTITY_INVALID")]
    [InlineData("PLN", -2, "RFQ_QUANTITY_INVALID")]
    public async Task Update_rejects_invalid_fields_without_changing_row_or_version(
        string currency, int quantity, string expectedError)
    {
        var repository = new QuoteRequestRepository(db.DataSource);
        var original = await repository.CreateAsync(new(
            PostgresFixture.TenantId, Guid.NewGuid(), null, 3, QuoteStatus.New, "PLN"));
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PutAsJsonAsync(UpdateUrl(original.Id),
            new { expectedRowVersion = original.RowVersion, requestedQuantity = quantity, currency });

        await AssertValidationProblemAsync(response, expectedError);
        Assert.Equal(original, await repository.FindAsync(original.TenantId, original.Id));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("part")]
    public async Task Update_rejects_empty_reference_identifier_without_mutation(string field)
    {
        var repository = new QuoteRequestRepository(db.DataSource);
        var original = await repository.CreateAsync(new(
            PostgresFixture.TenantId, Guid.NewGuid(), null, 3, QuoteStatus.New));
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PutAsJsonAsync(UpdateUrl(original.Id), new
        {
            expectedRowVersion = original.RowVersion,
            customerId = field == "customer" ? Guid.Empty : (Guid?)null,
            partRevisionId = field == "part" ? Guid.Empty : (Guid?)null
        });

        await AssertValidationProblemAsync(response, "RFQ_REFERENCE_ID_INVALID");
        Assert.Equal(original, await repository.FindAsync(original.TenantId, original.Id));
    }

    [Fact]
    public async Task Valid_draft_create_and_update_remain_supported()
    {
        var id = Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var createdResponse = await api.Client.PostAsJsonAsync(CreateUrl,
            new { id, requestedQuantity = 3, currency = "PLN" });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var original = await new QuoteRequestRepository(db.DataSource)
            .FindAsync(PostgresFixture.TenantId, id);
        Assert.NotNull(original);

        using var updateResponse = await api.Client.PutAsJsonAsync(UpdateUrl(id),
            new { expectedRowVersion = original.RowVersion, requestedQuantity = 4, currency = "EUR" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await new QuoteRequestRepository(db.DataSource)
            .FindAsync(PostgresFixture.TenantId, id);
        Assert.NotNull(updated);
        Assert.Equal(4, updated.RequestedQuantity);
        Assert.Equal("EUR", updated.Currency);
        Assert.Equal(original.RowVersion + 1, updated.RowVersion);
    }

    [Fact]
    public async Task Malformed_json_is_still_a_client_error()
    {
        using var api = new RfqFileApiContext(db.ConnectionString);
        using var body = new StringContent("{\"currency\":", Encoding.UTF8, "application/json");
        using var response = await api.Client.PostAsync(CreateUrl, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<long> StoredCountAsync(Guid id)
    {
        await using var command = db.DataSource.CreateCommand(
            "SELECT count(*) FROM quote_requests WHERE tenant_id=@tenant AND id=@id");
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertValidationProblemAsync(
        HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("DOMAIN_VALIDATION_FAILED", problem.GetProperty("code").GetString());
        Assert.Equal(expectedError, problem.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }
}
