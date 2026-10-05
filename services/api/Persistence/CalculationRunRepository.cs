using Npgsql;
using NpgsqlTypes;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class CalculationRunRepository(NpgsqlDataSource dataSource) : ICalculationRunRepository
{
    public async Task<StoredCalculation> SaveAsync(CalculationWrite calculation, CancellationToken cancellationToken = default)
    {
        var tenant = calculation.Snapshot.TenantId;
        var id = StableId.FromHash(calculation.CalculationHash);
        var status = calculation.TimeResult.Status == CalculationStatus.Success && calculation.StockResult.Status == CalculationStatus.Success
            && (calculation.CostResult is null || calculation.CostResult.Status == CalculationStatus.Success)
            ? CalculationStatus.Success : CalculationStatus.Blocked;
        var errors = calculation.TimeResult.Errors.Concat(calculation.StockResult.Errors)
            .Concat(calculation.CostResult?.Errors ?? []).Select(e => e.Code);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT set_config('app.correlation_id',@correlation,true),set_config('app.audit_source','CalculationService',true)", connection))
        {
            command.Parameters.AddWithValue("correlation", calculation.CorrelationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        bool inserted;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO calculation_runs(id,tenant_id,quote_snapshot_id,engine_version,time_engine_version,stock_engine_version,
                cost_engine_version,calculation_hash,status,started_at,finished_at,error_code,result_json,correlation_id,duration_ms)
            VALUES(@id,@tenant,@snapshot,@engine,@time,@stock,@cost,@hash,@status,@started,@finished,@error,@json,@correlation,@duration)
            ON CONFLICT (tenant_id,quote_snapshot_id,engine_version,calculation_hash) DO NOTHING RETURNING id
            """, connection))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("snapshot", calculation.Snapshot.Id);
            command.Parameters.AddWithValue("engine", calculation.EngineVersion);
            command.Parameters.AddWithValue("time", calculation.TimeResult.EngineVersion);
            command.Parameters.AddWithValue("stock", calculation.StockResult.EngineVersion);
            command.Parameters.AddWithValue("cost", NpgsqlDbType.Text,
                (object?)calculation.CostResult?.EngineVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("hash", calculation.CalculationHash);
            command.Parameters.AddWithValue("status", status.ToString());
            command.Parameters.AddWithValue("started", calculation.StartedAt.ToUniversalTime());
            command.Parameters.AddWithValue("finished", calculation.FinishedAt.ToUniversalTime());
            command.Parameters.AddWithValue("error", NpgsqlDbType.Text, status == CalculationStatus.Success ? DBNull.Value : string.Join("|", errors));
            command.Parameters.AddWithValue("json", calculation.ResultJson);
            command.Parameters.AddWithValue("correlation", calculation.CorrelationId);
            command.Parameters.AddWithValue("duration", calculation.DurationMs);
            inserted = await command.ExecuteScalarAsync(cancellationToken) is Guid;
        }
        if (inserted)
        {
            var costByOperation = calculation.CostResult?.OperationResults
                .ToDictionary(x => x.OperationNo, StringComparer.Ordinal);
            foreach (var operation in calculation.TimeResult.OperationResults)
            {
                CostOperationTrace? costOperation = null;
                if (costByOperation is not null)
                    costByOperation.TryGetValue(operation.OperationNo, out costOperation);
                await using var command = new NpgsqlCommand("""
                    INSERT INTO calculation_operation_results(id,tenant_id,calculation_run_id,process_operation_id,
                        operation_no,sequence_no,quantity,unit_tj_sec,batch_tpz_min,unit_tpz_sec,unit_labor_sec,
                        rate_overall_pln_h,rate_tpz_pln_h,tj_cost_unit,tpz_cost_unit,labor_cost_unit)
                    SELECT @id,@tenant,@run,o.id,@number,@sequence,@quantity,@tj,@tpz,@unit_tpz,@labor,
                        @rate_overall,@rate_tpz,@tj_cost,@tpz_cost,@labor_cost
                    FROM process_operations o
                    WHERE o.tenant_id=@tenant AND o.route_id=@source_route AND o.operation_no=@number
                    """, connection);
                command.Parameters.AddWithValue("id", StableId.FromText(calculation.CalculationHash + "|" + operation.OperationNo));
                command.Parameters.AddWithValue("tenant", tenant);
                command.Parameters.AddWithValue("run", id);
                command.Parameters.AddWithValue("number", operation.OperationNo);
                command.Parameters.AddWithValue("sequence", operation.SequenceNo);
                command.Parameters.AddWithValue("quantity", calculation.TimeResult.Quantity);
                command.Parameters.AddWithValue("tj", operation.UnitTjSec);
                command.Parameters.AddWithValue("tpz", operation.BatchTpzMin);
                command.Parameters.AddWithValue("unit_tpz", operation.UnitTpzSec);
                command.Parameters.AddWithValue("labor", operation.UnitLaborSec);
                command.Parameters.AddWithValue("rate_overall", NpgsqlDbType.Numeric,
                    (object?)costOperation?.RateOverallPlnH ?? DBNull.Value);
                command.Parameters.AddWithValue("rate_tpz", NpgsqlDbType.Numeric,
                    (object?)costOperation?.RateTpzPlnH ?? DBNull.Value);
                command.Parameters.AddWithValue("tj_cost", NpgsqlDbType.Numeric,
                    (object?)costOperation?.TjCostUnit ?? DBNull.Value);
                command.Parameters.AddWithValue("tpz_cost", NpgsqlDbType.Numeric,
                    (object?)costOperation?.TpzCostUnit ?? DBNull.Value);
                command.Parameters.AddWithValue("labor_cost", NpgsqlDbType.Numeric,
                    (object?)costOperation?.LaborCostUnit ?? DBNull.Value);
                command.Parameters.AddWithValue("source_route", calculation.Snapshot.SourceRouteId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Historical operation reference could not be resolved uniquely.");
            }
        }
        await using var read = new NpgsqlCommand("""
            SELECT id,quote_snapshot_id,calculation_hash,engine_version,result_json,status FROM calculation_runs
            WHERE tenant_id=@tenant AND calculation_hash=@hash
            """, connection);
        read.Parameters.AddWithValue("tenant", tenant);
        read.Parameters.AddWithValue("hash", calculation.CalculationHash);
        StoredCalculation stored;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Calculation insert/read failed.");
            stored = Read(reader, tenant);
        }
        if (!StringComparer.Ordinal.Equals(stored.ResultJson, calculation.ResultJson))
            throw new InvalidOperationException("DETERMINISM_VIOLATION: the same calculation hash produced different results.");
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    public async Task<StoredCalculation?> FindAsync(Guid tenantId, string calculationHash, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,quote_snapshot_id,calculation_hash,engine_version,result_json,status FROM calculation_runs
            WHERE tenant_id=@tenant AND calculation_hash=@hash
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("hash", calculationHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, tenantId) : null;
    }

    private static StoredCalculation Read(NpgsqlDataReader reader, Guid tenantId) => new(reader.GetGuid(0), tenantId,
        reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<CalculationStatus>(reader.GetString(5)));
}
