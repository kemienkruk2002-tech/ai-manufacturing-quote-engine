using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application;

public sealed record CalculationResponse(QuoteSnapshotPayload Snapshot, TimeCalculationResult Time,
    StockCalculationResult Stock, CostCalculationResult? Cost, string SnapshotHash, string CalculationHash, string EngineVersion,
    CalculationStatus Status);

public sealed class CalculationService(ISnapshotInputRepository inputs, IQuoteSnapshotRepository snapshots,
    ICalculationRunRepository runs, TimeProvider clock, ILogger<CalculationService> logger)
{
    public const string EngineVersion = "quote-engine-stage1-v1";
    public const string CostEnginePipelineVersion = "quote-engine-cost-v1";

    public async Task<CalculationResponse> CalculateAsync(SnapshotRequest request, string correlationId,
        CancellationToken cancellationToken = default)
    {
        var start = clock.GetUtcNow();
        var ticks = Stopwatch.GetTimestamp();
        string? snapshotHash = null;
        string? calculationHash = null;
        var requestedEngineVersion = request.RuleVersions.CanonicalSchema == CanonicalCostSnapshotSerializer.SchemaVersion
            ? CostEnginePipelineVersion : EngineVersion;
        logger.LogInformation("calculation_started correlation_id={correlation_id} engine_version={engine_version}", correlationId, requestedEngineVersion);
        try
        {
            var prepared = await inputs.BuildAsync(request, cancellationToken);
            var payload = prepared.Payload;
            ValidateVersions(payload);
            var snapshot = await snapshots.GetOrCreateAsync(request.QuoteRequestId, prepared, cancellationToken);
            snapshotHash = snapshot.SnapshotHash;
            var engineVersion = EngineVersionFor(payload);
            calculationHash = CanonicalSnapshotSerializer.ComputeCalculationHash(snapshotHash, engineVersion);
            var response = Replay(snapshot);
            var resultJson = SerializeResult(response.Time, response.Stock, response.Cost, response.EngineVersion);
            var duration = ElapsedMilliseconds(ticks);
            await runs.SaveAsync(new CalculationWrite(snapshot, payload, engineVersion, calculationHash, response.Time,
                response.Stock, response.Cost, resultJson, correlationId, start, clock.GetUtcNow(), duration), cancellationToken);
            logger.LogInformation("calculation_finished correlation_id={correlation_id} snapshot_hash={snapshot_hash} calculation_hash={calculation_hash} duration_ms={duration_ms} status={status}",
                correlationId, snapshotHash, calculationHash, ElapsedMilliseconds(ticks), response.Status);
            return response;
        }
        catch (Exception error)
        {
            // Do not log request payloads, customer files, SQL values or connection strings.
            logger.Log(error is MissingSnapshotInputException or DomainValidationException ? LogLevel.Warning : LogLevel.Error,
                "calculation_finished correlation_id={correlation_id} snapshot_hash={snapshot_hash} calculation_hash={calculation_hash} duration_ms={duration_ms} status={status} error_type={error_type}",
                correlationId, snapshotHash, calculationHash, ElapsedMilliseconds(ticks),
                error is MissingSnapshotInputException ? "Blocked" : error is DomainValidationException ? "ValidationError" : "Failed", error.GetType().Name);
            throw;
        }
    }

    /// <summary>Replays historical input entirely from stored bytes; no master-data, rates or clock reads.</summary>
    public static CalculationResponse Replay(StoredSnapshot snapshot)
    {
        var payload = CanonicalSnapshotDispatcher.Deserialize(snapshot.CanonicalJson);
        if (payload.TenantId != snapshot.TenantId || CanonicalSnapshotDispatcher.ComputeSnapshotHash(payload) != snapshot.SnapshotHash
            || CanonicalSnapshotDispatcher.Serialize(payload) != snapshot.CanonicalJson)
            throw new InvalidOperationException("SNAPSHOT_INTEGRITY_ERROR: canonical bytes/hash/tenant mismatch.");
        ValidateVersions(payload);
        var time = new TimeEngineV1().Calculate(new TimeCalculationInput(payload.Route, payload.Quantity));
        var stock = new StockEngineV1().Calculate(new StockCalculationInput(payload.PartRevision.FinalMassKg, payload.Stock));
        var cost = payload.RuleVersions.CanonicalSchema == CanonicalCostSnapshotSerializer.SchemaVersion
            ? new CostEngineV1().Calculate(new CostCalculationInput(payload.Route, payload.Quantity, payload.Cost)) : null;
        var engineVersion = EngineVersionFor(payload);
        return new CalculationResponse(payload, time, stock, cost, snapshot.SnapshotHash,
            CanonicalSnapshotSerializer.ComputeCalculationHash(snapshot.SnapshotHash, engineVersion), engineVersion,
            time.Status == CalculationStatus.Success && stock.Status == CalculationStatus.Success
                && (cost is null || cost.Status == CalculationStatus.Success) ? CalculationStatus.Success : CalculationStatus.Blocked);
    }

    public static string SerializeResult(TimeCalculationResult time, StockCalculationResult stock)
        => SerializeResult(time, stock, null, EngineVersion);

    public static string SerializeResult(TimeCalculationResult time, StockCalculationResult stock,
        CostCalculationResult? cost, string engineVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("engineVersion", engineVersion);
            writer.WritePropertyName("time");
            writer.WriteRawValue(CanonicalSnapshotSerializer.SerializeTimeResult(time));
            writer.WritePropertyName("stock");
            writer.WriteStartObject();
            writer.WriteString("engineVersion", stock.EngineVersion);
            writer.WriteString("status", stock.Status.ToString());
            Decimal(writer, "finalMassKg", stock.FinalMassKg);
            Decimal(writer, "normMassKgPerUnit", stock.NormMassKgPerUnit);
            Decimal(writer, "materialUtilization", stock.MaterialUtilization);
            Decimal(writer, "scrapFraction", stock.ScrapFraction);
            writer.WriteStartArray("errors");
            foreach (var error in stock.Errors)
            {
                writer.WriteStartObject();
                writer.WriteString("code", error.Code);
                writer.WriteString("message", error.Message);
                writer.WriteString("operationNo", error.OperationNo);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            if (cost is not null)
            {
                writer.WritePropertyName("cost");
                writer.WriteRawValue(SerializeCostResult(cost));
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeCostResult(CostCalculationResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("engineVersion", result.EngineVersion);
            writer.WriteString("status", result.Status.ToString());
            writer.WriteNumber("quantity", result.Quantity);
            Decimal(writer, "materialCostUnit", result.MaterialCostUnit);
            Decimal(writer, "laborTjCostUnit", result.LaborTjCostUnit);
            Decimal(writer, "laborTpzCostUnit", result.LaborTpzCostUnit);
            Decimal(writer, "laborCostUnit", result.LaborCostUnit);
            Decimal(writer, "totalCostUnit", result.TotalCostUnit);
            writer.WriteStartArray("operationResults");
            foreach (var operation in result.OperationResults)
            {
                writer.WriteStartObject();
                writer.WriteString("operationNo", operation.OperationNo);
                writer.WriteNumber("sequenceNo", operation.SequenceNo);
                Decimal(writer, "tjSec", operation.TjSec);
                Decimal(writer, "tpzMinPerBatch", operation.TpzMinPerBatch);
                writer.WriteNumber("quantity", operation.Quantity);
                Decimal(writer, "rateOverallPlnH", operation.RateOverallPlnH);
                Decimal(writer, "rateTpzPlnH", operation.RateTpzPlnH);
                writer.WriteString("tjFormula", operation.TjFormula);
                writer.WriteString("tpzFormula", operation.TpzFormula);
                Decimal(writer, "tjCostUnit", operation.TjCostUnit);
                Decimal(writer, "tpzCostUnit", operation.TpzCostUnit);
                Decimal(writer, "laborCostUnit", operation.LaborCostUnit);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("trace");
            if (result.Trace is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                Decimal(writer, "materialCostUnit", result.Trace.MaterialCostUnit);
                writer.WriteString("currency", result.Trace.Currency);
                writer.WriteString("machineRateVersion", result.Trace.MachineRateVersion);
                writer.WriteString("costEngineVersion", result.Trace.CostEngineVersion);
                writer.WriteString("laborTjFormula", result.Trace.LaborTjFormula);
                writer.WriteString("laborTpzFormula", result.Trace.LaborTpzFormula);
                writer.WriteString("laborFormula", result.Trace.LaborFormula);
                writer.WriteString("totalFormula", result.Trace.TotalFormula);
                writer.WriteEndObject();
            }
            writer.WriteStartArray("errors");
            foreach (var error in result.Errors.OrderBy(x => x.Code, StringComparer.Ordinal)
                .ThenBy(x => x.OperationNo, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("code", error.Code);
                writer.WriteString("message", error.Message);
                writer.WriteString("operationNo", error.OperationNo);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Decimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToString("0.############################", CultureInfo.InvariantCulture));
    }

    private static void ValidateVersions(QuoteSnapshotPayload payload)
    {
        var stageOne = payload.RuleVersions == new RuleVersions(TimeEngineV1.Version, StockEngineV1.Version, CanonicalSnapshotSerializer.SchemaVersion)
            && payload.Cost is null;
        var cost = payload.RuleVersions == new RuleVersions(TimeEngineV1.Version, StockEngineV1.Version, CanonicalCostSnapshotSerializer.SchemaVersion)
            && payload.Cost?.CostEngineVersion == CostEngineV1.Version;
        if (!stageOne && !cost)
            throw new MissingSnapshotInputException("RULE_VERSION_UNSUPPORTED", "The snapshot requires an unavailable engine or canonical schema version.");
    }
    private static string EngineVersionFor(QuoteSnapshotPayload payload) =>
        payload.RuleVersions.CanonicalSchema == CanonicalCostSnapshotSerializer.SchemaVersion
            ? CostEnginePipelineVersion : EngineVersion;
    private static decimal ElapsedMilliseconds(long started) => (Stopwatch.GetTimestamp() - started) * 1000m / Stopwatch.Frequency;
}
