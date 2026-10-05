using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RepeatabilityTests
{
    [Fact]
    public void One_hundred_serializations_hashes_and_calculations_are_identical()
    {
        var snapshot = GoldenFixture.Load();
        var engine = new TimeEngineV1();
        var canonicalSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var snapshotHashes = new HashSet<string>(StringComparer.Ordinal);
        var calculationHashes = new HashSet<string>(StringComparer.Ordinal);
        var results = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 100; index++)
        {
            var result = engine.Calculate(new(snapshot.Route, snapshot.Quantity));
            Assert.Equal(CalculationStatus.Success, result.Status);
            canonicalSnapshots.Add(CanonicalSnapshotSerializer.Serialize(snapshot));
            var snapshotHash = CanonicalSnapshotSerializer.ComputeSnapshotHash(snapshot);
            snapshotHashes.Add(snapshotHash);
            calculationHashes.Add(CanonicalSnapshotSerializer.ComputeCalculationHash(snapshotHash, result.EngineVersion));
            results.Add(CanonicalSnapshotSerializer.SerializeTimeResult(result));
        }
        Assert.Single(canonicalSnapshots);
        Assert.Single(snapshotHashes);
        Assert.Single(calculationHashes);
        Assert.Single(results);
    }

    [Fact]
    public void Stored_canonical_payload_can_be_replayed_to_identical_time_result()
    {
        var snapshot = GoldenFixture.Load();
        var canonical = CanonicalSnapshotSerializer.Serialize(snapshot);
        var replay = CanonicalSnapshotSerializer.Deserialize(canonical);
        var engine = new TimeEngineV1();
        Assert.Equal(CanonicalSnapshotSerializer.SerializeTimeResult(engine.Calculate(new(snapshot.Route, snapshot.Quantity))),
            CanonicalSnapshotSerializer.SerializeTimeResult(engine.Calculate(new(replay.Route, replay.Quantity))));
    }
}
