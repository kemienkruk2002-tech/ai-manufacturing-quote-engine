using System.Data;
using Npgsql;
using QuoteEngine.Persistence;
using Xunit.Sdk;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyConstraintTests(PostgresFixture db)
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Revision = "20000000-0000-0000-0000-000000000001";
    private const string Draft = "50000000-0000-0000-0000-000000000099";
    private const string Operation = "70000000-0000-0000-0000-000000000099";
    private const string Machine = "60000000-0000-0000-0000-000000000001";
    private const string Approve = $"UPDATE process_routes SET status='Approved' WHERE id='{Draft}'";
    private const string InvalidateOperation = $"UPDATE process_operations SET machine_id=NULL WHERE id='{Operation}'";
    private const string DraftSql = $"""
        INSERT INTO process_routes(id,tenant_id,part_revision_id,route_code,version,status)
        VALUES ('{Draft}','{Tenant}','{Revision}','concurrency-test','1','Draft');
        INSERT INTO process_operations(id,tenant_id,route_id,operation_no,sequence_no,operation_type,operation_name,machine_id,tj_sec,tpz_min_per_batch,origin,approval_status)
        VALUES ('{Operation}','{Tenant}','{Draft}','0010',1,'Cutting','Concurrency test','{Machine}',30,20,'Document','Approved');
        """;

    [Theory]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("Insert")]
    public async Task Committed_approval_rejects_a_waiting_operation_mutation(string mutation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var scope = await IsolatedSchema.CreateAsync(db, deadline.Token);
        await ExecuteAsync(scope.DataSource, DraftSql, deadline.Token);
        await using var approver = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var writer = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var approvalTransaction = await approver.BeginTransactionAsync(deadline.Token);
        await using var mutationTransaction = await writer.BeginTransactionAsync(deadline.Token);
        await ExecuteAsync(approver, Approve, deadline.Token);

        var mutationSql = mutation switch
        {
            "Update" => InvalidateOperation,
            "Delete" => $"DELETE FROM process_operations WHERE id='{Operation}'",
            "Insert" => $"""
                INSERT INTO process_operations(id,tenant_id,route_id,operation_no,sequence_no,operation_type,operation_name,machine_id,tj_sec,tpz_min_per_batch,origin,approval_status)
                VALUES (gen_random_uuid(),'{Tenant}','{Draft}','0020',2,'Cutting','Late operation',NULL,30,20,'Document','Proposed')
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var pendingMutation = ExecuteAsync(writer, mutationSql, deadline.Token);
        try
        {
            await WaitForBlockingAsync(scope.DataSource, writer.ProcessID, approver.ProcessID, pendingMutation, deadline.Token);
            await approvalTransaction.CommitAsync(deadline.Token);
            await AssertPostgresFailureAsync(pendingMutation, PostgresErrorCodes.CheckViolation, "APPROVED_ROUTE_IMMUTABLE");
            await mutationTransaction.RollbackAsync(deadline.Token);
            Assert.Equal("Approved", await ScalarAsync<string>(scope.DataSource, $"SELECT status FROM process_routes WHERE id='{Draft}'", deadline.Token));
            Assert.Equal(1L, await ScalarAsync<long>(scope.DataSource,
                $"SELECT count(*) FROM process_operations WHERE route_id='{Draft}' AND machine_id IS NOT NULL", deadline.Token));
        }
        finally { await FinishPendingAsync(pendingMutation, deadline); }
    }

    [Fact]
    public async Task Waiting_approval_rechecks_operation_invalidated_by_committed_writer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var scope = await IsolatedSchema.CreateAsync(db, deadline.Token);
        await ExecuteAsync(scope.DataSource, DraftSql, deadline.Token);
        await using var writer = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var approver = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var mutationTransaction = await writer.BeginTransactionAsync(deadline.Token);
        await using var approvalTransaction = await approver.BeginTransactionAsync(deadline.Token);
        await ExecuteAsync(writer, InvalidateOperation, deadline.Token);

        var pendingApproval = ExecuteAsync(approver, Approve, deadline.Token);
        try
        {
            await WaitForBlockingAsync(scope.DataSource, approver.ProcessID, writer.ProcessID, pendingApproval, deadline.Token);
            await mutationTransaction.CommitAsync(deadline.Token);
            await AssertPostgresFailureAsync(pendingApproval, PostgresErrorCodes.CheckViolation, "ROUTE_INCOMPLETE");
            await approvalTransaction.RollbackAsync(deadline.Token);
            Assert.Equal("Draft", await ScalarAsync<string>(scope.DataSource, $"SELECT status FROM process_routes WHERE id='{Draft}'", deadline.Token));
            Assert.True(await ScalarAsync<bool>(scope.DataSource, $"SELECT machine_id IS NULL FROM process_operations WHERE id='{Operation}'", deadline.Token));
        }
        finally { await FinishPendingAsync(pendingApproval, deadline); }
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Approval_from_stale_transaction_snapshot_gets_serialization_failure(IsolationLevel isolation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var scope = await IsolatedSchema.CreateAsync(db, deadline.Token);
        await ExecuteAsync(scope.DataSource, DraftSql, deadline.Token);
        await using var approver = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var writer = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var approvalTransaction = await approver.BeginTransactionAsync(isolation, deadline.Token);
        // Establish an MVCC snapshot before the other session changes a route child.
        await ExecuteAsync(approver, $"SELECT status FROM process_routes WHERE id='{Draft}'", deadline.Token);
        await using var mutationTransaction = await writer.BeginTransactionAsync(deadline.Token);
        await ExecuteAsync(writer, InvalidateOperation, deadline.Token);

        var pendingApproval = ExecuteAsync(approver, Approve, deadline.Token);
        try
        {
            await WaitForBlockingAsync(scope.DataSource, approver.ProcessID, writer.ProcessID, pendingApproval, deadline.Token);
            await mutationTransaction.CommitAsync(deadline.Token);
            await AssertPostgresFailureAsync(pendingApproval, PostgresErrorCodes.SerializationFailure);
            await approvalTransaction.RollbackAsync(deadline.Token);
            Assert.Equal("Draft", await ScalarAsync<string>(scope.DataSource, $"SELECT status FROM process_routes WHERE id='{Draft}'", deadline.Token));
        }
        finally { await FinishPendingAsync(pendingApproval, deadline); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_overlapping_rate_observes_first_transaction_outcome(bool commitFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var scope = await IsolatedSchema.CreateAsync(db, deadline.Token);
        await using var first = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var second = await scope.DataSource.OpenConnectionAsync(deadline.Token);
        await using var firstTransaction = await first.BeginTransactionAsync(deadline.Token);
        await using var secondTransaction = await second.BeginTransactionAsync(deadline.Token);
        await ExecuteAsync(first, Rate("2026-01-01", "2026-03-01"), deadline.Token);

        var pendingRate = ExecuteAsync(second, Rate("2026-02-01", "2026-04-01"), deadline.Token);
        try
        {
            await WaitForBlockingAsync(scope.DataSource, second.ProcessID, first.ProcessID, pendingRate, deadline.Token);
            if (commitFirst)
            {
                await firstTransaction.CommitAsync(deadline.Token);
                await AssertPostgresFailureAsync(pendingRate, PostgresErrorCodes.ExclusionViolation);
            }
            else
            {
                await firstTransaction.RollbackAsync(deadline.Token);
                Assert.Equal(1, await pendingRate);
            }
            await secondTransaction.RollbackAsync(deadline.Token);
            Assert.Equal(commitFirst ? 1L : 0L, await ScalarAsync<long>(scope.DataSource, "SELECT count(*) FROM machine_rates", deadline.Token));
        }
        finally { await FinishPendingAsync(pendingRate, deadline); }
    }

    private static string Rate(string from, string to) => $"""
        INSERT INTO machine_rates(id,tenant_id,machine_id,rate_type,rate_pln_per_hour,effective_from,effective_to,rate_version)
        VALUES(gen_random_uuid(),'{Tenant}','{Machine}','Tj',100,'{from}T00:00:00Z','{to}T00:00:00Z','concurrency-v1')
        """;

    private static async Task WaitForBlockingAsync(NpgsqlDataSource source, int waiter, int blocker, Task pending, CancellationToken token)
    {
        using var observationTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        observationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var observer = await source.OpenConnectionAsync(observationTimeout.Token);
            await using var query = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@waiter))", observer);
            query.Parameters.AddWithValue("blocker", blocker);
            query.Parameters.AddWithValue("waiter", waiter);
            while (true)
            {
                if (pending.IsCompleted)
                {
                    await pending;
                    throw new XunitException("The competing write completed without waiting for its required PostgreSQL lock.");
                }
                if ((bool)(await query.ExecuteScalarAsync(observationTimeout.Token))!) return;
                // Poll the server's observed lock graph; elapsed time is never evidence of blocking.
                await Task.Delay(20, observationTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new XunitException($"PostgreSQL session {waiter} did not wait for session {blocker} within 10 seconds.");
        }
    }

    private static async Task AssertPostgresFailureAsync(Task pending, string sqlState, string? message = null)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
        Assert.Equal(sqlState, error.SqlState);
        if (message is not null) Assert.Contains(message, error.MessageText, StringComparison.Ordinal);
    }

    private static async Task FinishPendingAsync(Task pending, CancellationTokenSource deadline)
    {
        if (!pending.IsCompleted) await deadline.CancelAsync();
        // Observe and drain an in-flight command before transaction disposal on a failed assertion.
        try { await pending; }
        catch { /* Assertions above report the original failure; disposal rolls back open transactions. */ }
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 20 };
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> ExecuteAsync(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await ExecuteAsync(connection, sql, token);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(token))!;
    }

    private sealed class IsolatedSchema(PostgresFixture fixture, string schema, NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;

        public static async Task<IsolatedSchema> CreateAsync(PostgresFixture fixture, CancellationToken token)
        {
            // Approval must really commit to test the race. A dedicated schema also isolates
            // immutable committed rows from the shared golden fixture and other test counts.
            var schema = "quote_concurrency_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(fixture.DataSource, $"CREATE SCHEMA {schema}", token);
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = schema,
                CommandTimeout = 20
            };
            var scope = new IsolatedSchema(fixture, schema, NpgsqlDataSource.Create(settings.ConnectionString));
            try
            {
                var migrator = new DatabaseMigrator(scope.DataSource);
                await migrator.MigrateAsync(token);
                await migrator.SeedGoldenAsync(token);
                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            // The identifier is generated locally and never derived from user configuration.
            await ExecuteAsync(fixture.DataSource, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }
}
