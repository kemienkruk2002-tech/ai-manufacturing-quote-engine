using System.Text;
using System.Text.Json;

namespace QuoteEngine.Domain.Quoting;

/// <summary>Controlled extension of canonical-v1 with explicit material and operation rate inputs.</summary>
public static class CanonicalCostSnapshotSerializer
{
    public const string SchemaVersion = "canonical-v2-cost";

    public static string Serialize(QuoteSnapshotPayload payload)
    {
        if (payload.RuleVersions.CanonicalSchema != SchemaVersion || payload.Cost is null)
            throw new InvalidOperationException("canonical-v2-cost requires matching rule version and cost inputs.");
        using var baseDocument = JsonDocument.Parse(CanonicalSnapshotSerializer.Serialize(payload));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in baseDocument.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WritePropertyName("cost");
            writer.WriteStartObject();
            Decimal(writer, "material_cost_unit", payload.Cost.MaterialCostUnit);
            writer.WriteString("currency", payload.Cost.Currency);
            writer.WriteString("machine_rate_version", payload.Cost.MachineRateVersion);
            writer.WriteString("cost_engine_version", payload.Cost.CostEngineVersion);
            writer.WriteString("material_cost_source_reference", payload.Cost.MaterialCostSourceReference);
            writer.WritePropertyName("operation_rates");
            writer.WriteStartArray();
            foreach (var rate in payload.Cost.OperationRates)
            {
                writer.WriteStartObject();
                writer.WriteString("operation_no", rate.OperationNo);
                writer.WriteString("machine_code", rate.MachineCode);
                Decimal(writer, "rate_tpz_pln_h", rate.RateTpzPlnH);
                Decimal(writer, "rate_production_pln_h", rate.RateProductionPlnH);
                Decimal(writer, "rate_overall_pln_h", rate.RateOverallPlnH);
                writer.WriteString("source_reference", rate.SourceReference);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static QuoteSnapshotPayload Deserialize(string canonicalJson)
    {
        var payload = CanonicalSnapshotSerializer.Deserialize(canonicalJson);
        if (payload.RuleVersions.CanonicalSchema != SchemaVersion || payload.Cost is null)
            throw new JsonException("Expected a complete canonical-v2-cost payload.");
        return payload;
    }

    public static string ComputeSnapshotHash(QuoteSnapshotPayload payload) =>
        CanonicalSnapshotSerializer.ComputeSha256(Serialize(payload));

    private static void Decimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, CanonicalSnapshotSerializer.NormalizeDecimal(value.Value));
    }
}

public static class CanonicalSnapshotDispatcher
{
    public static string Serialize(QuoteSnapshotPayload payload) => payload.RuleVersions.CanonicalSchema switch
    {
        CanonicalSnapshotSerializer.SchemaVersion => CanonicalSnapshotSerializer.Serialize(payload),
        CanonicalCostSnapshotSerializer.SchemaVersion => CanonicalCostSnapshotSerializer.Serialize(payload),
        _ => throw new InvalidOperationException("Unsupported canonical snapshot schema version.")
    };

    public static QuoteSnapshotPayload Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var schema = document.RootElement.GetProperty("rule_versions").GetProperty("canonical_schema").GetString();
        return schema switch
        {
            CanonicalSnapshotSerializer.SchemaVersion => CanonicalSnapshotSerializer.Deserialize(json),
            CanonicalCostSnapshotSerializer.SchemaVersion => CanonicalCostSnapshotSerializer.Deserialize(json),
            _ => throw new JsonException("Unsupported canonical snapshot schema version.")
        };
    }

    public static string ComputeSnapshotHash(QuoteSnapshotPayload payload) =>
        CanonicalSnapshotSerializer.ComputeSha256(Serialize(payload));
}
