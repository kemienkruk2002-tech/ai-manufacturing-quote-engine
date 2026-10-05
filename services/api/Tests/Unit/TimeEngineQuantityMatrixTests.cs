using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.UnitTests;

public sealed class TimeEngineQuantityMatrixTests
{
    public static IEnumerable<object[]> Matrix =>
    [
        [1, 13800m, 15175m, 4.2152777778m],
        [5, 2760m, 4135m, 5.7430555556m],
        [10, 1380m, 2755m, 7.6527777778m],
        [25, 552m, 1927m, 13.3819444444m],
        [50, 276m, 1651m, 22.9305555556m],
        [100, 138m, 1513m, 42.0277777778m],
        [150, 92m, 1467m, 61.125m],
        [250, 55.2m, 1430.2m, 99.3194444444m],
        [500, 27.6m, 1402.6m, 194.8055555556m]
    ];

    public static IEnumerable<object[]> Quantities => Enumerable.Range(1, 500).Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Supplied_nine_quantity_examples_match_decimal_expectations(int quantity, decimal expectedTpz,
        decimal expectedLabor, decimal expectedHours)
    {
        var result = new TimeEngineV1().Calculate(new(GoldenFixture.Load().Route, quantity));
        Assert.Equal(expectedTpz, result.SumTpzSecPerUnit);
        Assert.Equal(expectedLabor, result.LaborSecPerUnit);
        Assert.InRange(Math.Abs(result.LaborHoursBatch!.Value - expectedHours), 0m, 0.000000001m);
    }

    [Theory]
    [MemberData(nameof(Quantities))]
    public void Every_quantity_1_through_500_obeys_batch_and_unit_invariants(int quantity)
    {
        var result = new TimeEngineV1().Calculate(new(GoldenFixture.Load().Route, quantity));
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal(1375m, result.SumTjSecPerUnit);
        Assert.Equal(230m, result.SumTpzMinPerBatch);
        Assert.Equal(13800m / quantity, result.SumTpzSecPerUnit);
        Assert.Equal(1375m + 13800m / quantity, result.LaborSecPerUnit);
        // Independent algebraic batch oracle; tolerate decimal division's final representational digit.
        Assert.InRange(Math.Abs(result.LaborHoursBatch!.Value - (1375m * quantity + 13800m) / 3600m),
            0m, 0.00000000000000000000001m);
        Assert.InRange(Math.Abs(result.OperationResults.Sum(x => x.UnitTpzSec) - result.SumTpzSecPerUnit!.Value),
            0m, 0.00000000000000000000001m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_quantity_is_validation_error(int quantity)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            new TimeEngineV1().Calculate(new(GoldenFixture.Load().Route, quantity)));
        Assert.Contains(error.Errors, x => x.Code == "QUANTITY_INVALID");
    }

    [Fact]
    public void Engine_accepts_other_parts_and_decimal_times_without_seed_rules_or_rounding()
    {
        var route = GoldenFixture.Load().Route;
        var operation = GoldenFixture.CopyOperation(route.Operations[0], tjSec: 1.234567m, tpzMin: 0.123456m);
        var result = new TimeEngineV1().Calculate(new(GoldenFixture.CopyRoute(route, [operation]), 7));
        Assert.Equal(1.234567m, result.SumTjSecPerUnit);
        Assert.Equal(0.123456m, result.SumTpzMinPerBatch);
        Assert.Equal(0.123456m * 60m / 7m, result.SumTpzSecPerUnit);
        Assert.NotEqual(decimal.Round(result.LaborHoursBatch!.Value, 2), result.LaborHoursBatch);
    }
}
