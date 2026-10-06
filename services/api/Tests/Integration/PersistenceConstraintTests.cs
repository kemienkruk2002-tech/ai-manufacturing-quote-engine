using Npgsql;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PersistenceConstraintTests(PostgresFixture db)
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Revision = "20000000-0000-0000-0000-000000000001";
    private const string Draft = "50000000-0000-0000-0000-000000000002";
    private const string Machine = "60000000-0000-0000-0000-000000000001";
    private const string DraftSql = $"""
        INSERT INTO process_routes(id,tenant_id,part_revision_id,route_code,version,status)
        VALUES ('{Draft}','{Tenant}','{Revision}','constraint-test','2','Draft');
        """;
    private static string Operation(string number = "0010", int sequence = 1, string tj = "30", string tpz = "20", string? machine = Machine) => $"""
        INSERT INTO process_operations(id,tenant_id,route_id,operation_no,sequence_no,operation_type,operation_name,machine_id,tj_sec,tpz_min_per_batch,origin,approval_status)
        VALUES (gen_random_uuid(),'{Tenant}','{Draft}','{number}',{sequence},'Cutting','Test',{(machine is null ? "NULL" : $"'{machine}'")},{tj},{tpz},'Document','Approved');
        """;

    [Fact]
    public async Task Empty_database_migrates_and_seed_is_repeatable()
    {
        var migrator = new DatabaseMigrator(db.DataSource);
        await migrator.MigrateAsync();
        await migrator.SeedGoldenAsync();
        await migrator.SeedGoldenAsync();
        Assert.Equal(9L, await db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM parts"));
        Assert.Equal(8L, await db.ScalarAsync<long>("SELECT count(*) FROM process_operations"));
        Assert.Equal(8L, await db.ScalarAsync<long>("SELECT count(*) FROM machines"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM machine_rates"));
        Assert.Equal(1375m, await db.ScalarAsync<decimal>("SELECT sum(tj_sec) FROM process_operations"));
        Assert.Equal(230m, await db.ScalarAsync<decimal>("SELECT sum(tpz_min_per_batch) FROM process_operations"));
    }

    [Fact]
    public Task Part_number_is_unique_within_tenant() => db.RejectAsync($"""
        INSERT INTO parts(id,tenant_id,part_number,name) VALUES(gen_random_uuid(),'{Tenant}','W07044','Duplicate')
        """, PostgresErrorCodes.UniqueViolation);

    [Fact]
    public Task Duplicate_operation_number_rejected() => db.RejectAsync(DraftSql + Operation() + Operation(sequence: 2), PostgresErrorCodes.UniqueViolation);

    [Fact]
    public Task Duplicate_sequence_rejected() => db.RejectAsync(DraftSql + Operation() + Operation(number: "0020"), PostgresErrorCodes.UniqueViolation);

    [Theory]
    [InlineData("-1", "20")]
    [InlineData("30", "-1")]
    public Task Negative_times_rejected(string tj, string tpz) => db.RejectAsync(DraftSql + Operation(tj: tj, tpz: tpz), PostgresErrorCodes.CheckViolation);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task Nonpositive_quantity_rejected(int quantity) => db.RejectAsync($"""
        INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
        VALUES(gen_random_uuid(),'{Tenant}','{Revision}',{quantity},'New')
        """, PostgresErrorCodes.CheckViolation);

    [Fact]
    public Task Empty_route_cannot_be_approved() => db.RejectAsync(DraftSql + $"UPDATE process_routes SET status='Approved' WHERE id='{Draft}'");

    [Fact]
    public Task Missing_machine_cannot_be_approved() => db.RejectAsync(DraftSql + Operation(machine: null) + $"UPDATE process_routes SET status='Approved' WHERE id='{Draft}'");

    [Theory]
    [InlineData("UPDATE process_routes SET version='changed' WHERE status='Approved'")]
    [InlineData("UPDATE process_routes SET status='Draft' WHERE status='Approved'")]
    [InlineData("DELETE FROM process_routes WHERE status='Approved'")]
    [InlineData("UPDATE process_operations SET tj_sec=31 WHERE operation_no='0010'")]
    [InlineData("DELETE FROM process_operations WHERE operation_no='0010'")]
    [InlineData("UPDATE inspection_requirements SET description='changed'")]
    [InlineData("DELETE FROM inspection_requirements")]
    public Task Approved_route_and_its_children_are_immutable(string sql) => db.RejectAsync(sql);

    [Fact]
    public Task Moving_operation_out_of_approved_route_is_rejected() => db.RejectAsync(DraftSql + $"UPDATE process_operations SET route_id='{Draft}' WHERE operation_no='0010'");

    [Fact]
    public Task Appending_operation_to_approved_route_is_rejected() => db.RejectAsync(Operation().Replace(Draft, PostgresFixture.RouteId.ToString()));

    [Fact]
    public Task Cross_tenant_reference_rejected() => db.RejectAsync($"""
        INSERT INTO tenants(id,name) VALUES ('00000000-0000-0000-0000-000000000002','Other tenant');
        INSERT INTO part_revisions(id,tenant_id,part_id,status)
        VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000001','Draft')
        """, PostgresErrorCodes.ForeignKeyViolation);

    [Fact]
    public Task Overlapping_rate_dates_rejected() => db.RejectAsync(Rate("2026-01-01", "2026-03-01") + Rate("2026-02-01", "2026-04-01"), PostgresErrorCodes.ExclusionViolation);

    [Fact]
    public Task Open_ended_rate_overlap_rejected() => db.RejectAsync(Rate("2026-01-01", null) + Rate("2026-02-01", null), PostgresErrorCodes.ExclusionViolation);

    [Fact]
    public async Task Adjacent_rate_intervals_are_allowed()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(Rate("2026-01-01", "2026-02-01") + Rate("2026-02-01", null), connection);
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Migration_checksum_detects_modified_history()
    {
        await using (var command = db.DataSource.CreateCommand("UPDATE schema_migrations SET checksum=repeat('0',64) WHERE version=(SELECT min(version) FROM schema_migrations) RETURNING version"))
        {
            var version = (string)(await command.ExecuteScalarAsync())!;
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseMigrator(db.DataSource).MigrateAsync());
            }
            finally
            {
                using var resource = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(version)!;
                using var reader = new StreamReader(resource);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(await reader.ReadToEndAsync()))).ToLowerInvariant();
                await using var restore = db.DataSource.CreateCommand("UPDATE schema_migrations SET checksum=@hash WHERE version=@version");
                restore.Parameters.AddWithValue("hash", hash);
                restore.Parameters.AddWithValue("version", version);
                await restore.ExecuteNonQueryAsync();
            }
        }
    }

    private static string Rate(string from, string? to) => $"""
        INSERT INTO machine_rates(id,tenant_id,machine_id,rate_type,rate_pln_per_hour,effective_from,effective_to,rate_version)
        VALUES(gen_random_uuid(),'{Tenant}','{Machine}','Tj',100,'{from}T00:00:00Z',{(to is null ? "NULL" : $"'{to}T00:00:00Z'")},'test-v1');
        """;
}
