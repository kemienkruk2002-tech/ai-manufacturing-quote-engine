using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class CanonicalCostSnapshotTests
{
    [Fact]
    public void Cost_snapshot_round_trips_and_preserves_canonical_v1()
    {
        var legacy = GoldenFixture.Load();
        var legacyJson = CanonicalSnapshotSerializer.Serialize(legacy);
        var payload = CostEngineTests.CostFixture();
        var json = CanonicalCostSnapshotSerializer.Serialize(payload);
        var replay = CanonicalCostSnapshotSerializer.Deserialize(json);
        Assert.Equal(json, CanonicalCostSnapshotSerializer.Serialize(replay));
        Assert.Equal(legacyJson, CanonicalSnapshotSerializer.Serialize(legacy));
        Assert.Contains("\"canonical_schema\":\"canonical-v2-cost\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("budget", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offer_price", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Any_rate_material_or_cost_engine_version_change_changes_both_hashes()
    {
        var payload = CostEngineTests.CostFixture();
        var cost = Assert.IsType<CostInputsSnapshot>(payload.Cost);
        var originalSnapshot = CanonicalCostSnapshotSerializer.ComputeSnapshotHash(payload);
        var originalCalculation = CanonicalSnapshotSerializer.ComputeCalculationHash(originalSnapshot,
            CalculationServiceVersion);
        var variants = new List<QuoteSnapshotPayload>
        {
            payload with { Cost = Copy(cost, cost.OperationRates, material: 14.95m) },
            payload with { Cost = Copy(cost, cost.OperationRates, engine: "cost-engine-v2") }
        };
        for (var index = 0; index < cost.OperationRates.Count; index++)
        {
            var rates = cost.OperationRates.ToArray();
            var rate = rates[index];
            foreach (var changed in new[]
                     {
                         rate with { RateTpzPlnH = rate.RateTpzPlnH + 0.01m },
                         rate with { RateProductionPlnH = rate.RateProductionPlnH + 0.01m },
                         rate with { RateOverallPlnH = rate.RateOverallPlnH + 0.01m }
                     })
            {
                var changedRates = rates.ToArray();
                changedRates[index] = changed;
                variants.Add(payload with { Cost = Copy(cost, changedRates) });
            }
        }
        foreach (var variant in variants)
        {
            var snapshotHash = CanonicalCostSnapshotSerializer.ComputeSnapshotHash(variant);
            Assert.NotEqual(originalSnapshot, snapshotHash);
            Assert.NotEqual(originalCalculation,
                CanonicalSnapshotSerializer.ComputeCalculationHash(snapshotHash, CalculationServiceVersion));
        }
    }

    private const string CalculationServiceVersion = "quote-engine-cost-v1";
    private static CostInputsSnapshot Copy(CostInputsSnapshot source,
        IReadOnlyList<OperationCostRateSnapshot> rates, decimal? material = null, string? engine = null) =>
        new(material ?? source.MaterialCostUnit, source.Currency, source.MachineRateVersion,
            engine ?? source.CostEngineVersion, rates, source.MaterialCostSourceReference);
}
