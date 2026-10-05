using System.Data;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Machines;
using QuoteEngine.Domain.Materials;
using QuoteEngine.Domain.Parts;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Persistence;

public sealed class SnapshotInputRepository(NpgsqlDataSource dataSource) :
    ISnapshotInputRepository, IPartRepository, IRouteRepository, IMachineRateRepository
{
    public async Task<PreparedSnapshot> BuildAsync(SnapshotRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Every component is read from one consistent PostgreSQL view, even during concurrent master-data edits.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        Guid revisionId;
        int quantity;
        await using (var command = new NpgsqlCommand("SELECT part_revision_id, requested_quantity FROM quote_requests WHERE tenant_id=@tenant AND id=@request", connection))
        {
            command.Parameters.AddWithValue("tenant", request.TenantId);
            command.Parameters.AddWithValue("request", request.QuoteRequestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw Missing("QUOTE_REQUEST_MISSING", "Quote request is missing for this tenant.");
            if (reader.IsDBNull(0)) throw Missing("PART_REVISION_MISSING", "Quote request has no recognized part revision.");
            revisionId = reader.GetGuid(0);
            if (reader.IsDBNull(1)) throw Missing("QUANTITY_MISSING", "Quote request has no confirmed quantity.");
            quantity = reader.GetInt32(1);
        }
        var part = await ReadPartAsync(connection, request.TenantId, revisionId, cancellationToken);
        var route = await ReadRouteAsync(connection, request.TenantId, revisionId, request.RouteCode, request.RouteVersion, cancellationToken);
        CostInputsSnapshot? cost = null;
        var rateStatus = "Missing:NotUsedByTimeOrStock";
        if (request.RuleVersions.CanonicalSchema == CanonicalCostSnapshotSerializer.SchemaVersion)
        {
            cost = await ReadCostAsync(connection, request.TenantId, request.QuoteRequestId, route.Id,
                request.MachineRateVersion, cancellationToken);
            rateStatus = "VersionedSnapshot";
        }
        var result = new QuoteSnapshotPayload(request.TenantId, part.Revision, part.Material, part.Stock, route.Snapshot, quantity,
            new MachineRatesReference(request.MachineRateVersion, rateStatus), request.RuleVersions, cost);
        await transaction.CommitAsync(cancellationToken);
        return new PreparedSnapshot(result, route.Id, revisionId);
    }

    public async Task<PartSnapshotData> GetRevisionAsync(Guid tenantId, Guid revisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadPartAsync(connection, tenantId, revisionId, cancellationToken);
    }

    public async Task<RouteSnapshot> GetApprovedAsync(Guid tenantId, Guid revisionId, string routeCode, string version, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var result = await ReadRouteAsync(connection, tenantId, revisionId, routeCode, version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result.Snapshot;
    }

    public async Task<decimal?> FindRateAsync(Guid tenantId, Guid machineId, RateType rateType, DateTimeOffset effectiveAt,
        string rateVersion, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT rate_pln_per_hour FROM machine_rates
            WHERE tenant_id=@tenant AND machine_id=@machine AND rate_type=@type AND rate_version=@version
              AND effective_from <= @at AND (effective_to IS NULL OR @at < effective_to)
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("machine", machineId);
        command.Parameters.AddWithValue("type", rateType.ToString());
        command.Parameters.AddWithValue("version", rateVersion);
        command.Parameters.AddWithValue("at", effectiveAt.ToUniversalTime());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (decimal)result;
    }

    private static async Task<PartSnapshotData> ReadPartAsync(NpgsqlConnection connection, Guid tenantId, Guid revisionId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.part_number,p.name,r.revision_code,r.variant,r.process_version,r.status,r.final_mass_kg,r.source_reference,
                m.material_code,m.standard_grade,m.display_name,m.source_reference,
                s.stock_type,s.diameter_mm,s.norm_mass_kg_per_unit,s.source_type,s.approval_status,s.source_reference,s.rule_version
            FROM part_revisions r
            JOIN parts p ON p.tenant_id=r.tenant_id AND p.id=r.part_id
            LEFT JOIN materials m ON m.tenant_id=r.tenant_id AND m.id=r.material_id
            LEFT JOIN stock_definitions s ON s.tenant_id=r.tenant_id AND s.id=r.stock_definition_id
            WHERE r.tenant_id=@tenant AND r.id=@revision
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("revision", revisionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw Missing("PART_REVISION_MISSING", "Part revision is missing for this tenant.");
        if (reader.IsDBNull(8)) throw Missing("MATERIAL_MISSING", "The part revision has no material.");
        if (reader.IsDBNull(12)) throw Missing("STOCK_MISSING", "The part revision has no stock definition.");
        return new PartSnapshotData(
            new PartRevisionSnapshot(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
                Enum.Parse<PartRevisionStatus>(reader.GetString(5)), Number(reader, 6), Text(reader, 7)),
            new MaterialSnapshot(reader.GetString(8), reader.GetString(9), reader.GetString(10), Text(reader, 11)),
            new StockSnapshot(Enum.Parse<StockType>(reader.GetString(12)), Number(reader, 13), Number(reader, 14),
                Enum.Parse<StockSourceType>(reader.GetString(15)), Enum.Parse<ApprovalStatus>(reader.GetString(16)), Text(reader, 17), Text(reader, 18)));
    }

    private static async Task<(Guid Id, RouteSnapshot Snapshot)> ReadRouteAsync(NpgsqlConnection connection, Guid tenantId, Guid revisionId, string routeCode, string version, CancellationToken token)
    {
        Guid routeId;
        string? source;
        await using (var command = new NpgsqlCommand("""
            SELECT id,source_reference FROM process_routes
            WHERE tenant_id=@tenant AND part_revision_id=@revision AND route_code=@code AND version=@version AND status='Approved'
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("revision", revisionId);
            command.Parameters.AddWithValue("code", routeCode);
            command.Parameters.AddWithValue("version", version);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw Missing("APPROVED_ROUTE_MISSING", "The requested approved route version does not exist.");
            routeId = reader.GetGuid(0);
            source = Text(reader, 1);
        }
        var inspections = new Dictionary<Guid, List<InspectionSnapshot>>();
        await using (var command = new NpgsqlCommand("""
            SELECT i.process_operation_id,i.requirement_no,i.requirement_type,i.description,i.measurement_method,
                i.sampling_text,i.protocol_text,i.origin,i.approval_status,i.source_reference
            FROM inspection_requirements i
            JOIN process_operations o ON o.tenant_id=i.tenant_id AND o.id=i.process_operation_id
            WHERE o.tenant_id=@tenant AND o.route_id=@route ORDER BY i.requirement_no
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("route", routeId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var operationId = reader.GetGuid(0);
                if (!inspections.TryGetValue(operationId, out var list)) inspections[operationId] = list = [];
                list.Add(new InspectionSnapshot(reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4),
                    Text(reader, 5), Text(reader, 6), Enum.Parse<InspectionOrigin>(reader.GetString(7)),
                    Enum.Parse<ApprovalStatus>(reader.GetString(8)), Text(reader, 9)));
            }
        }
        var operations = new List<OperationSnapshot>();
        await using (var command = new NpgsqlCommand("""
            SELECT o.id,o.operation_no,o.sequence_no,o.operation_name,m.machine_code,m.name,
                o.tj_sec,o.tpz_min_per_batch,o.origin,o.approval_status,o.setup_description,o.source_reference
            FROM process_operations o LEFT JOIN machines m ON m.tenant_id=o.tenant_id AND m.id=o.machine_id
            WHERE o.tenant_id=@tenant AND o.route_id=@route ORDER BY o.sequence_no
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("route", routeId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                operations.Add(new OperationSnapshot(reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : new MachineSnapshot(reader.GetString(4), reader.GetString(5)),
                    Number(reader, 6), Number(reader, 7), Enum.Parse<OperationOrigin>(reader.GetString(8)),
                    Enum.Parse<ApprovalStatus>(reader.GetString(9)), inspections.GetValueOrDefault(reader.GetGuid(0)) ?? [],
                    Text(reader, 10), Text(reader, 11)));
        }
        return (routeId, new RouteSnapshot(routeCode, version, RouteStatus.Approved, operations, source));
    }

    private static async Task<CostInputsSnapshot> ReadCostAsync(NpgsqlConnection connection, Guid tenantId,
        Guid quoteRequestId, Guid routeId, string rateVersion, CancellationToken token)
    {
        decimal? materialCost = null;
        string currency = "PLN";
        string? materialSource = null;
        await using (var command = new NpgsqlCommand("""
            SELECT material_cost_unit,currency,source_reference FROM quote_cost_inputs
            WHERE tenant_id=@tenant AND quote_request_id=@request
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("request", quoteRequestId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                materialCost = Number(reader, 0);
                currency = reader.GetString(1);
                materialSource = Text(reader, 2);
            }
        }

        var rates = new List<OperationCostRateSnapshot>();
        await using (var command = new NpgsqlCommand("""
            SELECT o.operation_no,m.machine_code,r.rate_tpz_pln_h,r.rate_production_pln_h,
                r.rate_overall_pln_h,r.source_reference
            FROM process_operations o
            LEFT JOIN machines m ON m.tenant_id=o.tenant_id AND m.id=o.machine_id
            LEFT JOIN machine_rate_versions r ON r.tenant_id=o.tenant_id AND r.machine_id=o.machine_id
                AND r.rate_version=@rate_version
            WHERE o.tenant_id=@tenant AND o.route_id=@route ORDER BY o.sequence_no
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("route", routeId);
            command.Parameters.AddWithValue("rate_version", rateVersion);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                rates.Add(new OperationCostRateSnapshot(reader.GetString(0), Text(reader, 1) ?? string.Empty,
                    Number(reader, 2), Number(reader, 3), Number(reader, 4), Text(reader, 5)));
        }
        return new CostInputsSnapshot(materialCost, currency, rateVersion, CostEngineV1.Version,
            rates, materialSource);
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static MissingSnapshotInputException Missing(string code, string message) => new(code, message);
}
