using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqDraftInputTests(PostgresFixture db)
{
    [Fact]
    public async Task Migrations_one_through_eleven_apply_from_scratch()
    {
        Assert.Equal(11L, await db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations"));
    }

    [Theory]
    [InlineData("New")]
    [InlineData("DataReview")]
    [InlineData("Blocked")]
    public async Task Draft_status_accepts_null_part_and_quantity(string status)
    {
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
            VALUES(gen_random_uuid(),@tenant,NULL,NULL,@status)
            """, ("tenant", PostgresFixture.TenantId), ("status", status));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
