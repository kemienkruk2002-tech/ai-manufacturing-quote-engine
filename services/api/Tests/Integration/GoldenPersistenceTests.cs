using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Machines;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class GoldenPersistenceTests(PostgresFixture db)
{
    private CalculationService Service => new(new SnapshotInputRepository(db.DataSource), new QuoteSnapshotRepository(db.DataSource),
        new CalculationRunRepository(db.DataSource), TimeProvider.System, NullLogger<CalculationService>.Instance);

    [Fact]
    public async Task W07044_database_flow_preserves_eight_operations_and_exact_golden_values()
    {
        var result = await Service.CalculateAsync(GoldenCase.Request, "integration-golden");
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal("W07044", result.Snapshot.PartRevision.PartNumber);
        Assert.Equal("Tuleja 92687521_A", result.Snapshot.PartRevision.Name);
        Assert.Equal("2", result.Snapshot.PartRevision.Variant);
        Assert.Equal("Podstawowa(1)", result.Snapshot.PartRevision.ProcessVersion);
        Assert.Null(result.Snapshot.PartRevision.RevisionCode);
        Assert.Equal("M00320", result.Snapshot.Material.Code);
        Assert.Equal("S355J2", result.Snapshot.Material.Grade);
        Assert.Equal(48m, result.Snapshot.Stock.DiameterMm);
        Assert.Equal(3.540m, result.Stock.FinalMassKg);
        Assert.Equal(5.298m, result.Stock.NormMassKgPerUnit);
        Assert.Equal(3.540m / 5.298m, result.Stock.MaterialUtilization);
        Assert.Equal(new[] { "0010", "0020", "0030", "0040", "0050", "0060", "0070", "0080" }, result.Snapshot.Route.Operations.Select(x => x.OperationNo));
        Assert.Equal(1375m, result.Time.SumTjSecPerUnit);
        Assert.Equal(230m, result.Time.SumTpzMinPerBatch);
        Assert.Equal(92m, result.Time.SumTpzSecPerUnit);
        Assert.Equal(1467m, result.Time.LaborSecPerUnit);
        Assert.Equal(61.125m, result.Time.LaborHoursBatch);
        Assert.Equal("Częściowo", result.Snapshot.Route.Operations.Single(x => x.OperationNo == "0060")
            .InspectionRequirements.Single(x => x.RequirementType == "Spline").ProtocolRequirementText);
        Assert.Equal(8, result.Snapshot.Route.Operations.Sum(x => x.InspectionRequirements.Count));
        Assert.Null(await new SnapshotInputRepository(db.DataSource).FindRateAsync(PostgresFixture.TenantId,
            Guid.Parse("60000000-0000-0000-0000-000000000001"), RateType.Tj, new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), "rates-v1"));
    }

    [Fact]
    public async Task Stored_snapshot_replays_one_hundred_times_and_run_save_is_idempotent()
    {
        var response = await Service.CalculateAsync(GoldenCase.Request, "repeatability-1");
        var repository = new QuoteSnapshotRepository(db.DataSource);
        var snapshot = await repository.FindAsync(PostgresFixture.TenantId, response.SnapshotHash);
        Assert.NotNull(snapshot);
        var expected = CalculationService.SerializeResult(response.Time, response.Stock);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var replay = CalculationService.Replay(snapshot);
            Assert.Equal(response.SnapshotHash, replay.SnapshotHash);
            Assert.Equal(response.CalculationHash, replay.CalculationHash);
            Assert.Equal(expected, CalculationService.SerializeResult(replay.Time, replay.Stock));
        }
        var second = await Service.CalculateAsync(GoldenCase.Request, "repeatability-2");
        Assert.Equal(response.CalculationHash, second.CalculationHash);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM calculation_runs WHERE calculation_hash='{response.CalculationHash}'"));
        Assert.Equal(8L, await db.ScalarAsync<long>($"SELECT count(*) FROM calculation_operation_results o JOIN calculation_runs r ON r.id=o.calculation_run_id WHERE r.calculation_hash='{response.CalculationHash}'"));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM audit_events a JOIN calculation_runs r ON r.id=a.entity_id WHERE r.calculation_hash='{response.CalculationHash}' AND action='CALCULATION_RUN'"));
        Assert.Equal(expected, (await new CalculationRunRepository(db.DataSource).FindAsync(PostgresFixture.TenantId, response.CalculationHash))!.ResultJson);
    }

    [Fact]
    public async Task Stored_snapshot_rejects_update_delete_and_truncate()
    {
        var response = await Service.CalculateAsync(GoldenCase.Request, "immutability");
        await db.RejectAsync($"UPDATE quote_snapshots SET created_at=now() WHERE snapshot_hash='{response.SnapshotHash}'", PostgresErrorCodes.CheckViolation);
        await db.RejectAsync($"DELETE FROM quote_snapshots WHERE snapshot_hash='{response.SnapshotHash}'", PostgresErrorCodes.CheckViolation);
        await db.RejectAsync("TRUNCATE quote_snapshots CASCADE", PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Historical_snapshot_survives_master_data_change_and_new_snapshot_has_new_hash()
    {
        var before = await Service.CalculateAsync(GoldenCase.Request, "historical-before");
        var snapshots = new QuoteSnapshotRepository(db.DataSource);
        var stored = (await snapshots.FindAsync(PostgresFixture.TenantId, before.SnapshotHash))!;
        var originalName = before.Snapshot.Material.DisplayName;
        try
        {
            await using var update = db.DataSource.CreateCommand("UPDATE materials SET display_name=@name WHERE tenant_id=@tenant AND material_code='M00320'");
            update.Parameters.AddWithValue("name", originalName + " [integration change]");
            update.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await update.ExecuteNonQueryAsync();
            var after = await Service.CalculateAsync(GoldenCase.Request, "historical-after");
            Assert.NotEqual(before.SnapshotHash, after.SnapshotHash);
            Assert.NotEqual(before.CalculationHash, after.CalculationHash);
            var replay = CalculationService.Replay(stored);
            Assert.Equal(originalName, replay.Snapshot.Material.DisplayName);
            Assert.Equal(CalculationService.SerializeResult(before.Time, before.Stock), CalculationService.SerializeResult(replay.Time, replay.Stock));
        }
        finally
        {
            await using var restore = db.DataSource.CreateCommand("UPDATE materials SET display_name=@name WHERE tenant_id=@tenant AND material_code='M00320'");
            restore.Parameters.AddWithValue("name", originalName);
            restore.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Tenant_scoped_repositories_do_not_leak_other_tenant_data()
    {
        var response = await Service.CalculateAsync(GoldenCase.Request, "tenant-test");
        var other = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var inputs = new SnapshotInputRepository(db.DataSource);
        await Assert.ThrowsAsync<MissingSnapshotInputException>(() => inputs.BuildAsync(GoldenCase.Request with { TenantId = other }));
        await Assert.ThrowsAsync<MissingSnapshotInputException>(() => inputs.GetRevisionAsync(other, PostgresFixture.RevisionId));
        await Assert.ThrowsAsync<MissingSnapshotInputException>(() => inputs.GetApprovedAsync(other, PostgresFixture.RevisionId, "W07044", "1"));
        Assert.Null(await new QuoteSnapshotRepository(db.DataSource).FindAsync(other, response.SnapshotHash));
        Assert.Null(await new CalculationRunRepository(db.DataSource).FindAsync(other, response.CalculationHash));
    }

    [Fact]
    public async Task Nonterminating_decimal_results_round_trip_through_numeric_without_scale_loss()
    {
        var requestId = Guid.Parse("90000000-0000-0000-0000-000000000007");
        await using (var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
            VALUES(@id,@tenant,@revision,7,'New')
            """))
        {
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            command.Parameters.AddWithValue("revision", PostgresFixture.RevisionId);
            await command.ExecuteNonQueryAsync();
        }
        var response = await Service.CalculateAsync(GoldenCase.Request with { QuoteRequestId = requestId }, "numeric-q7");
        await using var read = db.DataSource.CreateCommand("""
            SELECT o.operation_no,o.unit_tpz_sec,o.unit_labor_sec FROM calculation_operation_results o
            JOIN calculation_runs r ON r.id=o.calculation_run_id WHERE r.calculation_hash=@hash ORDER BY o.sequence_no
            """);
        read.Parameters.AddWithValue("hash", response.CalculationHash);
        await using var reader = await read.ExecuteReaderAsync();
        foreach (var operation in response.Time.OperationResults)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(operation.OperationNo, reader.GetString(0));
            Assert.Equal(operation.UnitTpzSec, reader.GetDecimal(1));
            Assert.Equal(operation.UnitLaborSec, reader.GetDecimal(2));
        }
        Assert.False(await reader.ReadAsync());
    }
}
