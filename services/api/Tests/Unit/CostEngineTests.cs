using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class CostEngineTests
{
    private static readonly (string Operation, decimal Tj, decimal Tpz, decimal Labor)[] DisplayExpected =
    [
        ("0010", 1.52m, 0.32m, 1.84m),
        ("0020", 5.74m, 0.44m, 6.18m),
        ("0030", 8.93m, 0.42m, 9.35m),
        ("0040", 49.21m, 1.39m, 50.60m),
        ("0050", 16.78m, 1.02m, 17.80m),
        ("0060", 3.57m, 0.30m, 3.87m),
        ("0070", 1.50m, 0.13m, 1.63m),
        ("0080", 0.00m, 0.00m, 0.00m)
    ];

    [Fact]
    public void Golden_W07044_Q150_matches_display_values_without_intermediate_rounding()
    {
        var payload = CostFixture();
        var result = new CostEngineV1().Calculate(new(payload.Route, payload.Quantity, payload.Cost));
        var display = CostPresentation.Project(result);
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal(87.25m, display.LaborTjCostUnit);
        Assert.Equal(4.02m, display.LaborTpzCostUnit);
        Assert.Equal(91.27m, display.LaborCostUnit);
        Assert.Equal(14.94m, display.MaterialCostUnit);
        Assert.Equal(106.21m, display.TotalCostUnit);
        Assert.Equal(91.26437500000000000000000001m, result.LaborCostUnit);
        Assert.Equal(106.20437500000000000000000001m, result.TotalCostUnit);
        foreach (var expected in DisplayExpected)
        {
            var actual = display.Operations.Single(x => x.OperationNo == expected.Operation);
            Assert.Equal(expected.Tj, actual.TjCostUnit);
            Assert.Equal(expected.Tpz, actual.TpzCostUnit);
            Assert.Equal(expected.Labor, actual.LaborCostUnit);
        }
        Assert.Equal("material_cost_unit + labor_cost_unit", result.Trace!.TotalFormula);
        Assert.Equal(CostEngineV1.Version, result.Trace.CostEngineVersion);
    }

    [Fact]
    public void One_hundred_cost_calculations_are_full_precision_identical()
    {
        var payload = CostFixture();
        var results = Enumerable.Range(0, 100).Select(_ =>
            CalculationServiceShim.Serialize(new CostEngineV1().Calculate(new(payload.Route, payload.Quantity, payload.Cost)))).ToArray();
        Assert.Single(results.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void Missing_required_rate_blocks_instead_of_using_zero()
    {
        var payload = CostFixture();
        var cost = CopyCost(payload.Cost!, payload.Cost!.OperationRates.Where(x => x.OperationNo != "0040").ToArray());
        var result = new CostEngineV1().Calculate(new(payload.Route, payload.Quantity, cost));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "RATE_MISSING" && x.OperationNo == "0040");
        Assert.Null(result.TotalCostUnit);
    }

    [Fact]
    public void Missing_material_cost_blocks()
    {
        var payload = CostFixture();
        var result = new CostEngineV1().Calculate(new(payload.Route, payload.Quantity,
            CopyCost(payload.Cost!, payload.Cost!.OperationRates, materialCost: null, preserveNull: true)));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "MATERIAL_COST_MISSING");
    }

    [Fact]
    public void Negative_rate_is_validation_error()
    {
        var payload = CostFixture();
        var rates = payload.Cost!.OperationRates.ToArray();
        rates[0] = rates[0] with { RateOverallPlnH = -0.01m };
        var error = Assert.Throws<DomainValidationException>(() =>
            new CostEngineV1().Calculate(new(payload.Route, payload.Quantity, CopyCost(payload.Cost, rates))));
        Assert.Contains(error.Errors, x => x.Code == "RATE_NEGATIVE");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_quantity_remains_validation_error(int quantity)
    {
        var payload = CostFixture();
        var error = Assert.Throws<DomainValidationException>(() =>
            new CostEngineV1().Calculate(new(payload.Route, quantity, payload.Cost)));
        Assert.Contains(error.Errors, x => x.Code == "QUANTITY_INVALID");
    }

    internal static QuoteSnapshotPayload CostFixture()
    {
        var payload = GoldenFixture.Load();
        var values = new Dictionary<string, (decimal Tpz, decimal Production, decimal Overall)>
        {
            ["0010"] = (145.70m, 174.62m, 182.83m), ["0020"] = (130.76m, 147.51m, 147.51m),
            ["0030"] = (124.84m, 153.54m, 160.70m), ["0040"] = (208.88m, 249.19m, 295.23m),
            ["0050"] = (153.31m, 202.63m, 212.01m), ["0060"] = (132.79m, 160.65m, 160.65m),
            ["0070"] = (118.53m, 135.02m, 135.02m), ["0080"] = (0m, 0m, 0m)
        };
        var rates = payload.Route.Operations.Select(operation =>
        {
            var value = values[operation.OperationNo];
            return new OperationCostRateSnapshot(operation.OperationNo, operation.Machine!.Code,
                value.Tpz, value.Production, value.Overall, "Koszty.jpg");
        }).ToArray();
        return payload with
        {
            MachineRates = new("w07044-koszty-v1", "VersionedSnapshot"),
            RuleVersions = payload.RuleVersions with { CanonicalSchema = CanonicalCostSnapshotSerializer.SchemaVersion },
            Cost = new CostInputsSnapshot(14.94m, "PLN", "w07044-koszty-v1", CostEngineV1.Version,
                rates, "kalkulacja kosztu.jpg")
        };
    }

    private static CostInputsSnapshot CopyCost(CostInputsSnapshot source,
        IReadOnlyList<OperationCostRateSnapshot> rates, decimal? materialCost = null, bool preserveNull = false) =>
        new(preserveNull ? materialCost : materialCost ?? source.MaterialCostUnit, source.Currency,
            source.MachineRateVersion, source.CostEngineVersion, rates, source.MaterialCostSourceReference);

    private static class CalculationServiceShim
    {
        public static string Serialize(CostCalculationResult result) => string.Join('|',
            result.MaterialCostUnit, result.LaborTjCostUnit, result.LaborTpzCostUnit,
            result.LaborCostUnit, result.TotalCostUnit,
            string.Join(';', result.OperationResults.Select(x => $"{x.OperationNo}:{x.TjCostUnit}:{x.TpzCostUnit}:{x.LaborCostUnit}")));
    }
}
