using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SnapshotAssociationTests(PostgresFixture db)
{
    private CalculationService Service => new(new SnapshotInputRepository(db.DataSource),
        new QuoteSnapshotRepository(db.DataSource), new CalculationRunRepository(db.DataSource),
        TimeProvider.System, NullLogger<CalculationService>.Instance);

    [Fact]
    public async Task Identical_Q150_requests_share_content_and_calculation_but_keep_distinct_request_links()
    {
        var firstRequest = Id(101);
        var secondRequest = Id(102);
        var neverCalculatedRequest = Id(103);
        await CreateRequestAsync(firstRequest, 150);
        await CreateRequestAsync(secondRequest, 150);
        await CreateRequestAsync(neverCalculatedRequest, 150);

        var first = await Service.CalculateAsync(GoldenCase.Request with { QuoteRequestId = firstRequest }, "association-first");
        var second = await Service.CalculateAsync(GoldenCase.Request with { QuoteRequestId = secondRequest }, "association-second");
        Assert.Equal(CalculationStatus.Success, first.Status);
        Assert.Equal(CalculationStatus.Success, second.Status);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(first.CalculationHash, second.CalculationHash);

        var stored = await new QuoteSnapshotRepository(db.DataSource).FindAsync(PostgresFixture.TenantId, first.SnapshotHash);
        Assert.NotNull(stored);
        await using var links = db.DataSource.CreateCommand("""
            SELECT quote_request_id,quote_snapshot_id FROM quote_request_snapshots
            WHERE tenant_id=@tenant AND quote_request_id IN (@first,@second) ORDER BY quote_request_id
            """);
        links.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        links.Parameters.AddWithValue("first", firstRequest);
        links.Parameters.AddWithValue("second", secondRequest);
        var associatedRequests = new HashSet<Guid>();
        await using (var reader = await links.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                Assert.True(associatedRequests.Add(reader.GetGuid(0)), "Each request must have exactly one link for these inputs.");
                Assert.Equal(stored.Id, reader.GetGuid(1));
            }
        }
        Assert.Equal(2, associatedRequests.Count);
        Assert.Contains(firstRequest, associatedRequests);
        Assert.Contains(secondRequest, associatedRequests);
        // Creation provenance can predate either request (another test may calculate the golden case).
        // Association must come from the junction, never from the snapshot's first quote_request_id.
        Assert.Equal(0L, await CountLinksAsync(neverCalculatedRequest));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM quote_snapshots WHERE tenant_id=@tenant AND snapshot_hash=@hash
            """, ("tenant", PostgresFixture.TenantId), ("hash", first.SnapshotHash)));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM calculation_runs WHERE tenant_id=@tenant AND calculation_hash=@hash
            """, ("tenant", PostgresFixture.TenantId), ("hash", first.CalculationHash)));
    }

    [Fact]
    public async Task Saving_historical_calculation_after_request_revision_change_uses_snapshot_source_operations()
    {
        var requestId = Id(104);
        var changedRevisionId = Guid.Parse("20000000-0000-0000-0000-000000000104");
        // A distinct quantity guarantees this test inserts the operation results itself.
        await CreateRequestAsync(requestId, 173);
        var prepared = await new SnapshotInputRepository(db.DataSource)
            .BuildAsync(GoldenCase.Request with { QuoteRequestId = requestId });
        Assert.Equal(PostgresFixture.RouteId, prepared.SourceRouteId);
        Assert.Equal(PostgresFixture.RevisionId, prepared.SourceRevisionId);
        var stored = await new QuoteSnapshotRepository(db.DataSource).GetOrCreateAsync(requestId, prepared);
        var replay = CalculationService.Replay(stored);
        var runs = new CalculationRunRepository(db.DataSource);
        Assert.Null(await runs.FindAsync(PostgresFixture.TenantId, replay.CalculationHash));

        await ExecuteAsync("""
            INSERT INTO part_revisions(id,tenant_id,part_id,material_id,stock_definition_id,revision_code,
                variant,process_version,status,final_mass_kg,source_reference)
            SELECT @new_revision,tenant_id,part_id,material_id,stock_definition_id,'association-history',
                variant,process_version,'Draft',final_mass_kg,source_reference
            FROM part_revisions WHERE tenant_id=@tenant AND id=@old_revision
            """, ("new_revision", changedRevisionId), ("tenant", PostgresFixture.TenantId),
            ("old_revision", PostgresFixture.RevisionId));
        await ExecuteAsync("""
            UPDATE quote_requests SET part_revision_id=@revision WHERE tenant_id=@tenant AND id=@request
            """, ("revision", changedRevisionId), ("tenant", PostgresFixture.TenantId), ("request", requestId));
        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT count(*) FROM process_routes WHERE tenant_id=@tenant AND part_revision_id=@revision
            """, ("tenant", PostgresFixture.TenantId), ("revision", changedRevisionId)));

        var started = new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);
        var result = await runs.SaveAsync(new CalculationWrite(stored, prepared.Payload,
            CalculationService.EngineVersion, replay.CalculationHash, replay.Time, replay.Stock,
            null, CalculationService.SerializeResult(replay.Time, replay.Stock), "association-historical-save",
            started, started.AddMilliseconds(1), 1m));
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal(stored.Id, result.SnapshotId);

        await using var references = db.DataSource.CreateCommand("""
            SELECT result.operation_no,result.process_operation_id,source.operation_no,source.route_id,route.part_revision_id
            FROM calculation_operation_results result
            JOIN process_operations source ON source.tenant_id=result.tenant_id AND source.id=result.process_operation_id
            JOIN process_routes route ON route.tenant_id=source.tenant_id AND route.id=source.route_id
            WHERE result.tenant_id=@tenant AND result.calculation_run_id=@run ORDER BY result.sequence_no
            """);
        references.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
        references.Parameters.AddWithValue("run", result.Id);
        await using (var reader = await references.ExecuteReaderAsync())
        {
            foreach (var operation in replay.Time.OperationResults)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(operation.OperationNo, reader.GetString(0));
                Assert.NotEqual(Guid.Empty, reader.GetGuid(1));
                Assert.Equal(operation.OperationNo, reader.GetString(2));
                Assert.Equal(stored.SourceRouteId, reader.GetGuid(3));
                Assert.Equal(PostgresFixture.RevisionId, reader.GetGuid(4));
            }
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(8, replay.Time.OperationResults.Count);
        Assert.Equal(changedRevisionId, await ScalarAsync<Guid>("""
            SELECT part_revision_id FROM quote_requests WHERE tenant_id=@tenant AND id=@request
            """, ("tenant", PostgresFixture.TenantId), ("request", requestId)));
    }

    [Fact]
    public async Task Stale_prepared_quantity_is_blocked_before_snapshot_or_association_insert()
    {
        var requestId = Id(105);
        await CreateRequestAsync(requestId, 179);
        var prepared = await new SnapshotInputRepository(db.DataSource)
            .BuildAsync(GoldenCase.Request with { QuoteRequestId = requestId });
        var hash = CanonicalSnapshotSerializer.ComputeSnapshotHash(prepared.Payload);
        var snapshots = new QuoteSnapshotRepository(db.DataSource);
        Assert.Null(await snapshots.FindAsync(PostgresFixture.TenantId, hash));
        await ExecuteAsync("""
            UPDATE quote_requests SET requested_quantity=180 WHERE tenant_id=@tenant AND id=@request
            """, ("tenant", PostgresFixture.TenantId), ("request", requestId));

        var error = await Assert.ThrowsAsync<MissingSnapshotInputException>(() => snapshots.GetOrCreateAsync(requestId, prepared));
        Assert.Equal("STALE_CONFIGURATION", error.Code);
        Assert.Null(await snapshots.FindAsync(PostgresFixture.TenantId, hash));
        Assert.Equal(0L, await CountLinksAsync(requestId));
    }

    [Fact]
    public async Task Source_route_reference_is_foreign_key_constrained_and_immutable_after_insert()
    {
        var requestId = Id(106);
        await CreateRequestAsync(requestId, 181);
        var prepared = await new SnapshotInputRepository(db.DataSource)
            .BuildAsync(GoldenCase.Request with { QuoteRequestId = requestId });
        var snapshots = new QuoteSnapshotRepository(db.DataSource);
        var missingRoute = prepared with { SourceRouteId = Guid.Empty };
        var error = await Assert.ThrowsAsync<PostgresException>(() => snapshots.GetOrCreateAsync(requestId, missingRoute));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal(0L, await CountLinksAsync(requestId));

        var stored = await snapshots.GetOrCreateAsync(requestId, prepared);
        Assert.Equal(PostgresFixture.RouteId, stored.SourceRouteId);
        await db.RejectAsync($"UPDATE quote_snapshots SET source_route_id=source_route_id WHERE id='{stored.Id:D}'",
            PostgresErrorCodes.CheckViolation);
        Assert.Equal(PostgresFixture.RouteId, (await snapshots.FindAsync(PostgresFixture.TenantId, stored.SnapshotHash))!.SourceRouteId);
        Assert.Equal(1L, await CountLinksAsync(requestId));
    }

    [Fact]
    public async Task Concurrent_same_request_calculations_are_idempotent_across_repeated_rounds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        for (var round = 0; round < 4; round++)
        {
            var requestId = Id(107 + round);
            await CreateRequestAsync(requestId, 211 + round);
            var request = GoldenCase.Request with { QuoteRequestId = requestId };
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(index => Service.CalculateAsync(request,
                    $"association-concurrent-{round}-{index}", deadline.Token)));

            Assert.All(results, result => Assert.Equal(CalculationStatus.Success, result.Status));
            var snapshotHash = Assert.Single(results.Select(x => x.SnapshotHash).Distinct(StringComparer.Ordinal));
            var calculationHash = Assert.Single(results.Select(x => x.CalculationHash).Distinct(StringComparer.Ordinal));
            Assert.Equal(1L, await CountLinksAsync(requestId));
            Assert.Equal(1L, await ScalarAsync<long>("""
                SELECT count(*) FROM quote_snapshots WHERE tenant_id=@tenant AND snapshot_hash=@hash
                """, ("tenant", PostgresFixture.TenantId), ("hash", snapshotHash)));
            Assert.Equal(1L, await ScalarAsync<long>("""
                SELECT count(*) FROM calculation_runs WHERE tenant_id=@tenant AND calculation_hash=@hash
                """, ("tenant", PostgresFixture.TenantId), ("hash", calculationHash)));
            Assert.Equal(8L, await ScalarAsync<long>("""
                SELECT count(*) FROM calculation_operation_results result
                JOIN calculation_runs run ON run.tenant_id=result.tenant_id AND run.id=result.calculation_run_id
                WHERE run.tenant_id=@tenant AND run.calculation_hash=@hash
                """, ("tenant", PostgresFixture.TenantId), ("hash", calculationHash)));
        }
    }

    [Fact]
    public async Task Stable_id_collision_with_different_full_hash_is_rejected_explicitly()
    {
        var requestId = Id(120);
        await CreateRequestAsync(requestId, 223);
        var prepared = await new SnapshotInputRepository(db.DataSource)
            .BuildAsync(GoldenCase.Request with { QuoteRequestId = requestId });
        var stored = await new QuoteSnapshotRepository(db.DataSource).GetOrCreateAsync(requestId, prepared);
        var replay = CalculationService.Replay(stored);
        var repository = new CalculationRunRepository(db.DataSource);
        var started = new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);
        var resultJson = CalculationService.SerializeResult(replay.Time, replay.Stock);
        var firstHash = replay.CalculationHash;
        var replacementNibble = firstHash[32] == '0' ? '1' : '0';
        var collidingHash = firstHash[..32] + replacementNibble + firstHash[33..];

        Assert.NotEqual(firstHash, collidingHash);
        Assert.Equal(StableId.FromHash(firstHash), StableId.FromHash(collidingHash));

        var firstWrite = new CalculationWrite(stored, prepared.Payload, CalculationService.EngineVersion,
            firstHash, replay.Time, replay.Stock, null, resultJson, "stable-id-first",
            started, started.AddMilliseconds(1), 1m);
        var secondWrite = firstWrite with
        {
            CalculationHash = collidingHash,
            CorrelationId = "stable-id-collision"
        };

        var first = await repository.SaveAsync(firstWrite);
        Assert.Equal(firstHash, first.CalculationHash);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(secondWrite));
        Assert.Contains("CALCULATION_ID_COLLISION", error.Message, StringComparison.Ordinal);

        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM calculation_runs WHERE tenant_id=@tenant AND id=@id
            """, ("tenant", PostgresFixture.TenantId), ("id", StableId.FromHash(firstHash))));
        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT count(*) FROM calculation_runs WHERE tenant_id=@tenant AND calculation_hash=@hash
            """, ("tenant", PostgresFixture.TenantId), ("hash", collidingHash)));
    }

    private Task<int> CreateRequestAsync(Guid requestId, int quantity) => ExecuteAsync("""
        INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
        VALUES(@id,@tenant,@revision,@quantity,'New')
        """, ("id", requestId), ("tenant", PostgresFixture.TenantId),
        ("revision", PostgresFixture.RevisionId), ("quantity", quantity));

    private Task<long> CountLinksAsync(Guid requestId) => ScalarAsync<long>("""
        SELECT count(*) FROM quote_request_snapshots WHERE tenant_id=@tenant AND quote_request_id=@request
        """, ("tenant", PostgresFixture.TenantId), ("request", requestId));

    private async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Guid Id(int suffix) => Guid.Parse($"90000000-0000-0000-0000-{suffix:D12}");
}
