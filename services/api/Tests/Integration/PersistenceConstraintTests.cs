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
        Assert.Equal(11L, await db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations"));
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
}
