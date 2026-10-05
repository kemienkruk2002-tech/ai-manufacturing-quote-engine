using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Materials;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.UnitTests;

public sealed class GoldenW07044Tests
{
    [Fact]
    public void Golden_Q150_reproduces_source_route_and_exact_time_totals()
    {
        var snapshot = GoldenFixture.Load();
        Assert.Equal("W07044", snapshot.PartRevision.PartNumber);
        Assert.Equal("Tuleja 92687521_A", snapshot.PartRevision.Name);
        Assert.Equal("2", snapshot.PartRevision.Variant);
        Assert.Equal("Podstawowa(1)", snapshot.PartRevision.ProcessVersion);
        Assert.Null(snapshot.PartRevision.RevisionCode);
        Assert.Equal(3.54m, snapshot.PartRevision.FinalMassKg);
        Assert.Equal("S355J2", snapshot.Material.Grade);
        Assert.Equal(StockType.RoundBar, snapshot.Stock.Type);
        Assert.Equal(48m, snapshot.Stock.DiameterMm);
        Assert.Equal(5.298m, snapshot.Stock.NormMassKgPerUnit);
        Assert.Equal(RouteStatus.Approved, snapshot.Route.Status);
        Assert.Equal(new[] { "0010", "0020", "0030", "0040", "0050", "0060", "0070", "0080" },
            snapshot.Route.Operations.Select(x => x.OperationNo));
        Assert.Equal(new[] { "4.52", "4.63", "4.81", "3.2", "1.051", "5.51", "5.61", "0.7" },
            snapshot.Route.Operations.Select(x => x.Machine!.Code));
        Assert.All(snapshot.Route.Operations, operation =>
        {
            Assert.Equal(OperationOrigin.Document, operation.Origin);
            Assert.Equal(ApprovalStatus.Approved, operation.ApprovalStatus);
        });

        var result = new TimeEngineV1().Calculate(new(snapshot.Route, snapshot.Quantity));
        Assert.Equal(CalculationStatus.Success, result.Status);
        Assert.Empty(result.Errors);
        Assert.Equal(150, result.Quantity);
        Assert.Equal(1375m, result.SumTjSecPerUnit);
        Assert.Equal(230m, result.SumTpzMinPerBatch);
        Assert.Equal(92m, result.SumTpzSecPerUnit);
        Assert.Equal(1467m, result.LaborSecPerUnit);
        Assert.Equal(61.125m, result.LaborHoursBatch);
        Assert.Equal(TimeEngineV1.Version, result.EngineVersion);
        Assert.Equal(new decimal[] { 30, 140, 200, 600, 285, 80, 40, 0 }, result.OperationResults.Select(x => x.UnitTjSec));
        Assert.Equal(new decimal[] { 20, 30, 30, 60, 60, 20, 10, 0 }, result.OperationResults.Select(x => x.BatchTpzMin));
        Assert.Equal(new decimal[] { 8, 12, 12, 24, 24, 8, 4, 0 }, result.OperationResults.Select(x => x.UnitTpzSec));
        Assert.Equal(new decimal[] { 38, 152, 212, 624, 309, 88, 44, 0 }, result.OperationResults.Select(x => x.UnitLaborSec));
    }

    [Fact]
    public void Source_inspections_include_partial_protocol_without_boolean_inference()
    {
        var route = GoldenFixture.Load().Route;
        Assert.Equal(8, route.Operations.Sum(x => x.InspectionRequirements.Count));
        var spline = Assert.Single(route.Operations.Single(x => x.OperationNo == "0060")
            .InspectionRequirements, x => x.RequirementType == "SPLINE");
        Assert.Equal("Częściowo", spline.ProtocolRequirementText);
        Assert.Equal("5 / sztuka ustawcza", spline.SamplingText);
    }
}
