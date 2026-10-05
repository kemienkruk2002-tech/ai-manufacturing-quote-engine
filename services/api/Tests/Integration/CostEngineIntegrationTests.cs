using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CostEngineIntegrationTests(PostgresFixture db)
{
    private CalculationService Service => new(new SnapshotInputRepository(db.DataSource),
        new QuoteSnapshotRepository(db.DataSource), new CalculationRunRepository(db.DataSource),
        TimeProvider.System, NullLogger<CalculationService>.Instance);

    [Fact]
    public async Task Golden_W07044_cost_is_persisted_with_full_precision_breakdown()
    {
        var result = await Service.CalculateAsync(GoldenCase.CostRequest, "cost-golden");
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.NotNull(result.Cost);
        var display = CostPresentation.Project(result.Cost);
        Assert.Equal(87.25m, display.LaborTjCostUnit);
        Assert.Equal(4.02m, display.LaborTpzCostUnit);
        Assert.Equal(91.27m, display.LaborCostUnit);
        Assert.Equal(14.94m, display.MaterialCostUnit);
        Assert.Equal(106.21m, display.TotalCostUnit);
        Assert.Equal(8, result.Cost.OperationResults.Count);
        Assert.Equal(8L, await db.ScalarAsync<long>("SELECT count(*) FROM machine_rate_versions WHERE rate_version='w07044-koszty-v1'"));
        Assert.Equal(CostEngineV1.Version, await ScalarAsync<string>("""
            SELECT cost_engine_version FROM calculation_runs WHERE calculation_hash=@hash
            """, ("hash", result.CalculationHash)));
        Assert.Equal(8L, await ScalarAsync<long>("""
            SELECT count(*) FROM calculation_operation_results o
            JOIN calculation_runs r ON r.id=o.calculation_run_id
            WHERE r.calculation_hash=@hash AND o.tj_cost_unit IS NOT NULL
              AND o.tpz_cost_unit IS NOT NULL AND o.labor_cost_unit IS NOT NULL
            """, ("hash", result.CalculationHash)));

        foreach (var operation in result.Cost.OperationResults)
        {
            await using var command = db.DataSource.CreateCommand("""
                SELECT rate_overall_pln_h,rate_tpz_pln_h,tj_cost_unit,tpz_cost_unit,labor_cost_unit
                FROM calculation_operation_results o JOIN calculation_runs r ON r.id=o.calculation_run_id
                WHERE r.calculation_hash=@hash AND o.operation_no=@operation
                """);
            command.Parameters.AddWithValue("hash", result.CalculationHash);
            command.Parameters.AddWithValue("operation", operation.OperationNo);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(operation.RateOverallPlnH, reader.GetDecimal(0));
            Assert.Equal(operation.RateTpzPlnH, reader.GetDecimal(1));
            Assert.Equal(operation.TjCostUnit, reader.GetDecimal(2));
            Assert.Equal(operation.TpzCostUnit, reader.GetDecimal(3));
            Assert.Equal(operation.LaborCostUnit, reader.GetDecimal(4));
        }
    }

    [Fact]
    public async Task Historical_cost_replay_does_not_read_current_material_or_rate_tables()
    {
        var original = await Service.CalculateAsync(GoldenCase.CostRequest, "cost-replay-before");
        var stored = (await new QuoteSnapshotRepository(db.DataSource)
            .FindAsync(PostgresFixture.TenantId, original.SnapshotHash))!;
        var expected = CalculationService.SerializeResult(original.Time, original.Stock, original.Cost, original.EngineVersion);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE quote_cost_inputs SET material_cost_unit=999
            WHERE tenant_id=@tenant AND quote_request_id=@request
            """, connection))
        {
            update.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            update.Parameters.AddWithValue("request", PostgresFixture.RequestId);
            await update.ExecuteNonQueryAsync();
        }
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var replay = CalculationService.Replay(stored);
            Assert.Equal(original.SnapshotHash, replay.SnapshotHash);
            Assert.Equal(original.CalculationHash, replay.CalculationHash);
            Assert.Equal(expected, CalculationService.SerializeResult(replay.Time, replay.Stock, replay.Cost, replay.EngineVersion));
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Missing_rate_version_returns_blocked_and_never_substitutes_zero()
    {
        var request = GoldenCase.CostRequest with { MachineRateVersion = "missing-rates-v1" };
        var result = await Service.CalculateAsync(request, "cost-missing-rate");
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Equal(CalculationStatus.Blocked, result.Cost!.Status);
        Assert.Contains(result.Cost.Errors, x => x.Code == "RATE_OVERALL_MISSING");
        Assert.Null(result.Cost.TotalCostUnit);
    }

    [Fact]
    public async Task Missing_material_cost_input_returns_blocked()
    {
        var requestId = Guid.Parse("90000000-0000-0000-0000-000000000201");
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
            VALUES(@request,@tenant,@revision,150,'New')
            """, ("request", requestId), ("tenant", PostgresFixture.TenantId),
            ("revision", PostgresFixture.RevisionId));
        var result = await Service.CalculateAsync(GoldenCase.CostRequest with { QuoteRequestId = requestId }, "cost-missing-material");
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Cost!.Errors, x => x.Code == "MATERIAL_COST_MISSING");
        Assert.Null(result.Cost.TotalCostUnit);
    }

    [Fact]
    public Task Database_rejects_negative_versioned_rates() => db.RejectAsync("""
        INSERT INTO machine_rate_versions(id,tenant_id,machine_id,rate_version,rate_tpz_pln_h,
            rate_production_pln_h,rate_overall_pln_h,source_reference)
        VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001',
            '60000000-0000-0000-0000-000000000001','negative-test',1,1,-0.01,'test')
        """, PostgresErrorCodes.CheckViolation);

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
