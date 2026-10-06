using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqCreateApiTests(PostgresFixture db)
{
    [Fact]
    public async Task Create_without_status_creates_New_draft()
    {
        var id = Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PostAsJsonAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs",
            new { id, externalRfqNo = $"RFQ-{id:N}" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New", body.GetProperty("status").GetString());
        Assert.Equal("New", await StoredStatusAsync(id));
    }

    [Fact]
    public async Task Create_with_explicit_New_status_remains_supported()
    {
        var id = Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PostAsJsonAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs",
            new { id, status = QuoteStatus.New.ToString(), requestedQuantity = 2 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("New", await StoredStatusAsync(id));
    }

    [Theory]
    [InlineData(QuoteStatus.DataReview)]
    [InlineData(QuoteStatus.ReadyForCalc)]
    [InlineData(QuoteStatus.Calculated)]
    [InlineData(QuoteStatus.Approved)]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Lost)]
    [InlineData(QuoteStatus.Won)]
    [InlineData(QuoteStatus.Blocked)]
    public async Task Create_rejects_explicit_non_New_status_without_persisting(QuoteStatus status)
    {
        var id = Guid.NewGuid();
        using var api = new RfqFileApiContext(db.ConnectionString);

        using var response = await api.Client.PostAsJsonAsync(
            $"/api/tenants/{PostgresFixture.TenantId}/rfqs",
            new
            {
                id,
                partRevisionId = PostgresFixture.RevisionId,
                requestedQuantity = 1,
                status = status.ToString(),
                currency = "PLN"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RFQ_CREATE_STATUS_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal(0L, await StoredCountAsync(id));
    }

    private async Task<string?> StoredStatusAsync(Guid id)
    {
        await using var command = db.DataSource.CreateCommand(
            "SELECT status FROM quote_requests WHERE tenant_id=@tenant AND id=@id");
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<long> StoredCountAsync(Guid id)
    {
        await using var command = db.DataSource.CreateCommand(
            "SELECT count(*) FROM quote_requests WHERE tenant_id=@tenant AND id=@id");
        command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
