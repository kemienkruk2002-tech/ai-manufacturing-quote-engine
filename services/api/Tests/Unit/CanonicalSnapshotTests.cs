using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.UnitTests;

public sealed class CanonicalSnapshotTests
{
    [Fact]
    public void Inspection_origin_supports_AiExtracted_as_stored_metadata_without_AI_execution()
    {
        var snapshot = GoldenFixture.Load();
        var operations = snapshot.Route.Operations.ToArray();
        var inspections = operations[2].InspectionRequirements.ToArray();
        inspections[0] = inspections[0] with { Origin = InspectionOrigin.AiExtracted };
        operations[2] = GoldenFixture.CopyOperation(operations[2], inspections: inspections);
        var payload = snapshot with { Route = GoldenFixture.CopyRoute(snapshot.Route, operations) };
        var canonical = CanonicalSnapshotSerializer.Serialize(payload);
        var replay = CanonicalSnapshotSerializer.Deserialize(canonical);
        Assert.Equal(InspectionOrigin.AiExtracted, replay.Route.Operations[2].InspectionRequirements[0].Origin);
        Assert.Equal(canonical, CanonicalSnapshotSerializer.Serialize(replay));
        Assert.NotEqual(CanonicalSnapshotSerializer.ComputeSnapshotHash(snapshot), CanonicalSnapshotSerializer.ComputeSnapshotHash(replay));
    }

    [Fact]
    public void Canonical_contract_uses_explicit_property_order_decimal_strings_and_no_runtime_metadata()
    {
        var json = CanonicalSnapshotSerializer.Serialize(GoldenFixture.Load());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(new[] { "tenant_id", "part_revision", "material", "stock", "route", "quantity", "machine_rates", "rule_versions" },
            root.EnumerateObject().Select(x => x.Name));
        Assert.Equal(new[] { "part_number", "name", "revision_code", "variant", "process_version", "status", "final_mass_kg", "source_reference" },
            root.GetProperty("part_revision").EnumerateObject().Select(x => x.Name));
        Assert.Equal("3.54", root.GetProperty("part_revision").GetProperty("final_mass_kg").GetString());
        Assert.Equal("5.298", root.GetProperty("stock").GetProperty("norm_mass_kg_per_unit").GetString());
        Assert.DoesNotContain("created_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("approved_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("operation_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("route_id", json, StringComparison.Ordinal);
        Assert.Equal(json, CanonicalSnapshotSerializer.Serialize(CanonicalSnapshotSerializer.Deserialize(json)));
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void Serialization_is_culture_independent_and_normalizes_decimal_scale(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var snapshot = GoldenFixture.Load();
            var expected = CanonicalSnapshotSerializer.Serialize(snapshot);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var sameValues = snapshot with
            {
                PartRevision = snapshot.PartRevision with { FinalMassKg = 3.540000000m },
                Stock = snapshot.Stock with { NormMassKgPerUnit = 5.298000000000m, DiameterMm = 48.000000m }
            };
            Assert.Equal(expected, CanonicalSnapshotSerializer.Serialize(sameValues));
            Assert.Equal("0", CanonicalSnapshotSerializer.NormalizeDecimal(-0.0000m));
            Assert.Equal("0.0000000000000000000000000001", CanonicalSnapshotSerializer.NormalizeDecimal(0.0000000000000000000000000001m));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Incoming_route_and_requirement_order_do_not_change_snapshot_hash()
    {
        var snapshot = GoldenFixture.Load();
        var reversed = snapshot.Route.Operations.Reverse()
            .Select(x => GoldenFixture.CopyOperation(x, inspections: x.InspectionRequirements.Reverse().ToArray())).ToArray();
        var equivalent = snapshot with { Route = GoldenFixture.CopyRoute(snapshot.Route, reversed) };
        Assert.Equal(CanonicalSnapshotSerializer.Serialize(snapshot), CanonicalSnapshotSerializer.Serialize(equivalent));
        Assert.Equal(CanonicalSnapshotSerializer.ComputeSnapshotHash(snapshot), CanonicalSnapshotSerializer.ComputeSnapshotHash(equivalent));
    }

    [Fact]
    public void Every_material_input_and_version_change_produces_a_new_snapshot_hash()
    {
        var snapshot = GoldenFixture.Load();
        var operations = snapshot.Route.Operations.ToArray();
        operations[0] = GoldenFixture.CopyOperation(operations[0], tjSec: 30.000001m);
        var changedInspectionOperations = snapshot.Route.Operations.ToArray();
        var inspections = changedInspectionOperations[5].InspectionRequirements.ToArray();
        inspections[0] = inspections[0] with { ProtocolRequirementText = "Tak" };
        changedInspectionOperations[5] = GoldenFixture.CopyOperation(changedInspectionOperations[5], inspections: inspections);
        QuoteSnapshotPayload[] variants =
        [
            snapshot with { Quantity = 151 },
            snapshot with { PartRevision = snapshot.PartRevision with { FinalMassKg = 3.55m } },
            snapshot with { Material = snapshot.Material with { Grade = "S235" } },
            snapshot with { Stock = snapshot.Stock with { NormMassKgPerUnit = 5.3m } },
            snapshot with { Route = GoldenFixture.CopyRoute(snapshot.Route, operations) },
            snapshot with { Route = GoldenFixture.CopyRoute(snapshot.Route, changedInspectionOperations) },
            snapshot with { RuleVersions = snapshot.RuleVersions with { TimeEngine = "time-engine-v2" } },
            snapshot with { MachineRates = snapshot.MachineRates with { Version = "rates-v2" } }
        ];
        var originalHash = CanonicalSnapshotSerializer.ComputeSnapshotHash(snapshot);
        Assert.All(variants, variant => Assert.NotEqual(originalHash, CanonicalSnapshotSerializer.ComputeSnapshotHash(variant)));
        Assert.Equal(variants.Length, variants.Select(CanonicalSnapshotSerializer.ComputeSnapshotHash).Distinct().Count());
    }

    [Fact]
    public void Sha256_contract_matches_independent_utf8_and_delimiter_implementation()
    {
        var snapshot = GoldenFixture.Load();
        var json = CanonicalSnapshotSerializer.Serialize(snapshot);
        var expectedSnapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(expectedSnapshotHash, CanonicalSnapshotSerializer.ComputeSnapshotHash(snapshot));
        var expectedCalculationHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(expectedSnapshotHash + "|" + TimeEngineV1.Version))).ToLowerInvariant();
        Assert.Equal(expectedCalculationHash, CanonicalSnapshotSerializer.ComputeCalculationHash(expectedSnapshotHash, TimeEngineV1.Version));
        Assert.NotEqual(expectedCalculationHash, CanonicalSnapshotSerializer.ComputeCalculationHash(expectedSnapshotHash, "time-engine-v2"));
    }

    [Fact]
    public void Snapshot_defensively_copies_collections_and_exposes_no_in_place_edit()
    {
        var source = GoldenFixture.Load();
        var operations = source.Route.Operations.ToList();
        var inspections = operations[2].InspectionRequirements.ToList();
        operations[2] = GoldenFixture.CopyOperation(operations[2], inspections: inspections);
        var payload = source with { Route = GoldenFixture.CopyRoute(source.Route, operations) };
        var snapshot = new QuoteSnapshot(source.TenantId, Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"), payload,
            new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.FromHours(2)));
        var originalJson = snapshot.CanonicalJson;
        operations.Clear();
        inspections.Clear();
        Assert.Equal(8, snapshot.Payload.Route.Operations.Count);
        Assert.Equal(2, snapshot.Payload.Route.Operations[2].InspectionRequirements.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<OperationSnapshot>)snapshot.Payload.Route.Operations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<InspectionSnapshot>)snapshot.Payload.Route.Operations[2].InspectionRequirements).Clear());
        Assert.All(typeof(QuoteSnapshot).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Equal(originalJson, CanonicalSnapshotSerializer.Serialize(snapshot.Payload));
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAt.Offset);
    }

    [Fact]
    public void Persistence_metadata_does_not_change_the_calculation_payload()
    {
        var payload = GoldenFixture.Load();
        var first = new QuoteSnapshot(payload.TenantId, Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"), payload,
            DateTimeOffset.Parse("2026-09-15T00:00:00Z", CultureInfo.InvariantCulture));
        var later = new QuoteSnapshot(payload.TenantId, Guid.Parse("44444444-4444-4444-8444-444444444444"),
            Guid.Parse("55555555-5555-4555-8555-555555555555"), payload,
            DateTimeOffset.Parse("2027-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        Assert.Equal(first.CanonicalJson, later.CanonicalJson);
        Assert.Equal(first.SnapshotHash, later.SnapshotHash);
    }
}
