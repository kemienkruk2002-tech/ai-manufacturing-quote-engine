using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Domain.Quoting;

/// <summary>
/// Schema v1: explicitly ordered snake_case properties, sorted sequences, decimal strings in
/// invariant culture without trailing zeros, enum names as strings, UTF-8 without BOM.
/// Runtime IDs/timestamps are deliberately absent. Changing this contract requires a new schema version.
/// </summary>
public static class CanonicalSnapshotSerializer
{
    public const string SchemaVersion = "canonical-v1";
    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    public static string Serialize(QuoteSnapshotPayload payload) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("tenant_id", payload.TenantId.ToString("D"));
        writer.WritePropertyName("part_revision");
        writer.WriteStartObject();
        writer.WriteString("part_number", payload.PartRevision.PartNumber);
        writer.WriteString("name", payload.PartRevision.Name);
        writer.WriteString("revision_code", payload.PartRevision.RevisionCode);
        writer.WriteString("variant", payload.PartRevision.Variant);
        writer.WriteString("process_version", payload.PartRevision.ProcessVersion);
        writer.WriteString("status", payload.PartRevision.Status.ToString());
        Decimal(writer, "final_mass_kg", payload.PartRevision.FinalMassKg);
        writer.WriteString("source_reference", payload.PartRevision.SourceReference);
        writer.WriteEndObject();
        writer.WritePropertyName("material");
        writer.WriteStartObject();
        writer.WriteString("code", payload.Material.Code);
        writer.WriteString("grade", payload.Material.Grade);
        writer.WriteString("display_name", payload.Material.DisplayName);
        writer.WriteString("source_reference", payload.Material.SourceReference);
        writer.WriteEndObject();
        writer.WritePropertyName("stock");
        writer.WriteStartObject();
        writer.WriteString("type", payload.Stock.Type.ToString());
        Decimal(writer, "diameter_mm", payload.Stock.DiameterMm);
        Decimal(writer, "norm_mass_kg_per_unit", payload.Stock.NormMassKgPerUnit);
        writer.WriteString("source_type", payload.Stock.SourceType.ToString());
        writer.WriteString("approval_status", payload.Stock.ApprovalStatus.ToString());
        writer.WriteString("source_reference", payload.Stock.SourceReference);
        writer.WriteString("rule_version", payload.Stock.RuleVersion);
        writer.WriteEndObject();
        writer.WritePropertyName("route");
        writer.WriteStartObject();
        writer.WriteString("route_code", payload.Route.RouteCode);
        writer.WriteString("version", payload.Route.Version);
        writer.WriteString("status", payload.Route.Status.ToString());
        writer.WriteString("source_reference", payload.Route.SourceReference);
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        foreach (var operation in payload.Route.Operations)
        {
            writer.WriteStartObject();
            writer.WriteString("operation_no", operation.OperationNo);
            writer.WriteNumber("sequence_no", operation.SequenceNo);
            writer.WriteString("name", operation.Name);
            writer.WritePropertyName("machine");
            if (operation.Machine is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("code", operation.Machine.Code);
                writer.WriteString("name", operation.Machine.Name);
                writer.WriteEndObject();
            }
            Decimal(writer, "tj_sec", operation.TjSec);
            Decimal(writer, "tpz_min_per_batch", operation.TpzMinPerBatch);
            writer.WriteString("origin", operation.Origin.ToString());
            writer.WriteString("approval_status", operation.ApprovalStatus.ToString());
            writer.WriteString("setup_description", operation.SetupDescription);
            writer.WriteString("source_reference", operation.SourceReference);
            writer.WritePropertyName("inspection_requirements");
            writer.WriteStartArray();
            foreach (var inspection in operation.InspectionRequirements)
            {
                writer.WriteStartObject();
                writer.WriteString("requirement_no", inspection.RequirementNo);
                writer.WriteString("requirement_type", inspection.RequirementType);
                writer.WriteString("description", inspection.Description);
                writer.WriteString("measurement_method", inspection.MeasurementMethod);
                writer.WriteString("sampling_text", inspection.SamplingText);
                writer.WriteString("protocol_requirement_text", inspection.ProtocolRequirementText);
                writer.WriteString("origin", inspection.Origin.ToString());
                writer.WriteString("approval_status", inspection.ApprovalStatus.ToString());
                writer.WriteString("source_reference", inspection.SourceReference);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteNumber("quantity", payload.Quantity);
        writer.WritePropertyName("machine_rates");
        writer.WriteStartObject();
        writer.WriteString("version", payload.MachineRates.Version);
        writer.WriteString("status", payload.MachineRates.Status);
        writer.WriteEndObject();
        writer.WritePropertyName("rule_versions");
        writer.WriteStartObject();
        writer.WriteString("time_engine", payload.RuleVersions.TimeEngine);
        writer.WriteString("stock_engine", payload.RuleVersions.StockEngine);
        writer.WriteString("canonical_schema", payload.RuleVersions.CanonicalSchema);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static QuoteSnapshotPayload Deserialize(string canonicalJson) =>
        JsonSerializer.Deserialize<QuoteSnapshotPayload>(canonicalJson, ReadOptions)
        ?? throw new JsonException("Snapshot payload must be a JSON object.");

    public static string ComputeSnapshotHash(QuoteSnapshotPayload payload) => ComputeSha256(Serialize(payload));

    public static string ComputeCalculationHash(string snapshotHash, string engineVersion) =>
        ComputeSha256(snapshotHash + "|" + engineVersion);

    public static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string SerializeTimeResult(TimeCalculationResult result) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("status", result.Status.ToString());
        writer.WriteNumber("quantity", result.Quantity);
        Decimal(writer, "sum_tj_sec_per_unit", result.SumTjSecPerUnit);
        Decimal(writer, "sum_tpz_min_per_batch", result.SumTpzMinPerBatch);
        Decimal(writer, "sum_tpz_sec_per_unit", result.SumTpzSecPerUnit);
        Decimal(writer, "labor_sec_per_unit", result.LaborSecPerUnit);
        Decimal(writer, "labor_hours_batch", result.LaborHoursBatch);
        writer.WritePropertyName("operation_results");
        writer.WriteStartArray();
        foreach (var operation in result.OperationResults)
        {
            writer.WriteStartObject();
            writer.WriteString("operation_no", operation.OperationNo);
            writer.WriteNumber("sequence_no", operation.SequenceNo);
            Decimal(writer, "unit_tj_sec", operation.UnitTjSec);
            Decimal(writer, "batch_tpz_min", operation.BatchTpzMin);
            Decimal(writer, "unit_tpz_sec", operation.UnitTpzSec);
            Decimal(writer, "unit_labor_sec", operation.UnitLaborSec);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("engine_version", result.EngineVersion);
        writer.WritePropertyName("errors");
        writer.WriteStartArray();
        foreach (var error in result.Errors.OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.OperationNo, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("code", error.Code);
            writer.WriteString("message", error.Message);
            writer.WriteString("operation_no", error.OperationNo);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Decimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, NormalizeDecimal(value.Value));
    }

    public static string NormalizeDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new InvariantDecimalConverter());
        return options;
    }

    private sealed class InvariantDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? decimal.Parse(reader.GetString()!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                : reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(NormalizeDecimal(value));
    }
}
