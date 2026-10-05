using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Materials;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.UnitTests;

public sealed class StockEngineTests
{
    [Fact]
    public void Golden_stock_uses_approved_norm_separately_from_final_mass_without_rounding()
    {
        var snapshot = GoldenFixture.Load();
        var result = new StockEngineV1().Calculate(new(snapshot.PartRevision.FinalMassKg, snapshot.Stock));
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal(3.540m, result.FinalMassKg);
        Assert.Equal(5.298m, result.NormMassKgPerUnit);
        Assert.Equal(3.540m / 5.298m, result.MaterialUtilization);
        Assert.Equal(1m - 3.540m / 5.298m, result.ScrapFraction);
        Assert.InRange(Math.Abs(result.MaterialUtilization!.Value - 0.6681766704416761m), 0m, 0.0000000000000001m);
        Assert.InRange(Math.Abs(result.ScrapFraction!.Value - 0.3318233295583239m), 0m, 0.0000000000000001m);
        Assert.Equal(StockEngineV1.Version, result.EngineVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Nonpositive_norm_blocks_stock_calculation(string normText)
    {
        var snapshot = GoldenFixture.Load();
        var norm = decimal.Parse(normText, System.Globalization.CultureInfo.InvariantCulture);
        var result = new StockEngineV1().Calculate(new(snapshot.PartRevision.FinalMassKg,
            snapshot.Stock with { NormMassKgPerUnit = norm }));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "STOCK_NORM_INVALID");
        Assert.Null(result.MaterialUtilization);
        Assert.Null(result.ScrapFraction);
    }

    [Fact]
    public void Missing_norm_is_not_inferred_from_final_mass_or_bar_diameter()
    {
        var snapshot = GoldenFixture.Load();
        var result = new StockEngineV1().Calculate(new(snapshot.PartRevision.FinalMassKg,
            snapshot.Stock with { NormMassKgPerUnit = null }));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "STOCK_NORM_MISSING");
        Assert.Null(result.NormMassKgPerUnit);
        Assert.Null(result.MaterialUtilization);
    }

    [Fact]
    public void Missing_final_mass_and_unapproved_stock_are_explicit_blockers()
    {
        var result = new StockEngineV1().Calculate(new(null,
            GoldenFixture.Load().Stock with { ApprovalStatus = ApprovalStatus.Proposed }));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "FINAL_MASS_MISSING");
        Assert.Contains(result.Errors, x => x.Code == "STOCK_NOT_APPROVED");
        Assert.Null(result.MaterialUtilization);
    }

    [Fact]
    public void Missing_stock_is_explicit_blocker()
    {
        var result = new StockEngineV1().Calculate(new(3.54m, null));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "STOCK_MISSING");
        Assert.Null(result.NormMassKgPerUnit);
    }

    [Fact]
    public void Geometry_is_not_required_when_approved_norm_is_supplied()
    {
        var stock = GoldenFixture.Load().Stock with { DiameterMm = null, Type = StockType.Other };
        var result = new StockEngineV1().Calculate(new(2m, stock with { NormMassKgPerUnit = 3m }));
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Equal(2m / 3m, result.MaterialUtilization);
        Assert.Equal(1m - 2m / 3m, result.ScrapFraction);
    }
}
