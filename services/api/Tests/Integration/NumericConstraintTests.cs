using Npgsql;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NumericConstraintTests(PostgresFixture db)
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Draft = "5f000000-0000-0000-0000-000000000001";
    private const string Snapshot = "af000000-0000-0000-0000-000000000001";
    private const string Run = "bf000000-0000-0000-0000-000000000001";
    private static readonly string[] NonFiniteValues = ["NaN", "Infinity", "-Infinity"];

    public static IEnumerable<object[]> InputColumns()
    {
        var columns = new (string Table, string Column)[]
        {
            ("materials", "density_kg_m3"),
            ("stock_definitions", "diameter_mm"),
            ("stock_definitions", "width_mm"),
            ("stock_definitions", "height_mm"),
            ("stock_definitions", "thickness_mm"),
            ("stock_definitions", "length_mm"),
            ("stock_definitions", "allowance_mm"),
            ("stock_definitions", "kerf_mm"),
            ("stock_definitions", "norm_mass_kg_per_unit"),
            ("part_revisions", "final_mass_kg"),
            ("machines", "max_x_mm"),
            ("machines", "max_y_mm"),
            ("machines", "max_z_mm"),
            ("process_operations", "tj_sec"),
            ("process_operations", "tpz_min_per_batch"),
            ("process_operations", "confidence"),
            ("machine_rates", "rate_pln_per_hour")
        };
        foreach (var (table, column) in columns)
        foreach (var special in NonFiniteValues)
            yield return [table, column, special];
    }

    [Theory]
    [MemberData(nameof(InputColumns))]
    public Task Input_numeric_columns_reject_nonfinite_values(string table, string column, string special)
    {
        var setup = table switch
        {
            "process_operations" => $"""
                INSERT INTO process_routes(id,tenant_id,part_revision_id,route_code,version)
                VALUES ('{Draft}','{Tenant}','20000000-0000-0000-0000-000000000001','finite-input-test','1');
                INSERT INTO process_operations(id,tenant_id,route_id,operation_no,sequence_no,operation_type,operation_name,machine_id,tj_sec,tpz_min_per_batch,origin,approval_status)
                VALUES ('7f000000-0000-0000-0000-000000000001','{Tenant}','{Draft}','0010',1,'Cutting','Finite input test','60000000-0000-0000-0000-000000000001',30,20,'Document','Approved');
                """,
            "machine_rates" => $"""
                INSERT INTO machine_rates(id,tenant_id,machine_id,rate_type,rate_pln_per_hour,effective_from,rate_version)
                VALUES ('6f000000-0000-0000-0000-000000000001','{Tenant}','60000000-0000-0000-0000-000000000001','Tj',100,'2026-01-01T00:00:00Z','finite-input-test');
                """,
            _ => ""
        };
        var where = table == "process_operations" ? $"route_id='{Draft}'" : $"tenant_id='{Tenant}'";
        // All identifiers and literals come exclusively from the fixed theory data above.
        return RejectNonFiniteAsync(setup, $"UPDATE {table} SET {column}='{special}'::NUMERIC WHERE {where}");
    }

    public static IEnumerable<object[]> ResultColumns()
    {
        foreach (var column in new[] { "unit_tj_sec", "batch_tpz_min", "unit_tpz_sec", "unit_labor_sec" })
        foreach (var special in NonFiniteValues)
            yield return [column, special];
    }

    [Theory]
    [MemberData(nameof(ResultColumns))]
    public Task Calculated_numeric_columns_reject_nonfinite_values(string column, string special) =>
        RejectNonFiniteAsync(CalculationSetup + $"""
            INSERT INTO calculation_operation_results(id,tenant_id,calculation_run_id,process_operation_id,operation_no,sequence_no,quantity,unit_tj_sec,batch_tpz_min,unit_tpz_sec,unit_labor_sec)
            VALUES ('cf000000-0000-0000-0000-000000000001','{Tenant}','{Run}','70000000-0000-0000-0000-000000000001','0010',1,150,30,20,8,38);
            """, $"UPDATE calculation_operation_results SET {column}='{special}'::NUMERIC WHERE calculation_run_id='{Run}'");

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public Task Duration_rejects_nonfinite_values(string special) =>
        RejectNonFiniteAsync(CalculationSetup, $"UPDATE calculation_runs SET duration_ms='{special}'::NUMERIC WHERE id='{Run}'");

    private const string CalculationSetup = $$"""
        INSERT INTO quote_snapshots(id,tenant_id,quote_request_id,source_route_id,canonical_json,snapshot_hash)
        VALUES ('{{Snapshot}}','{{Tenant}}','90000000-0000-0000-0000-000000000001','50000000-0000-0000-0000-000000000001','{}',encode(sha256(convert_to('{}','UTF8')),'hex'));
        INSERT INTO calculation_runs(id,tenant_id,quote_snapshot_id,engine_version,time_engine_version,stock_engine_version,calculation_hash,status,started_at,finished_at,result_json,duration_ms)
        VALUES ('{{Run}}','{{Tenant}}','{{Snapshot}}','finite-result-test','time-engine-v1','stock-engine-v1',repeat('1',64),'Success','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','{}',0);
        """;

    private async Task RejectNonFiniteAsync(string setup, string mutation)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (setup.Length > 0)
        {
            await using var prepare = new NpgsqlCommand(setup, connection);
            await prepare.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(mutation, connection);
            await command.ExecuteNonQueryAsync();
        });
        // Constrained NUMERIC rejects infinities at type coercion; NaN reaches the explicit CHECK.
        Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.NumericValueOutOfRange });
        await transaction.RollbackAsync();
    }
}
