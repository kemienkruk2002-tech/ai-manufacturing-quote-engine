using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Machines;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.UnitTests;

public sealed class RouteInvariantTests
{
    private static readonly Guid RouteId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid RevisionId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public void Missing_or_empty_route_blocks_time_with_null_totals()
    {
        var source = GoldenFixture.Load().Route;
        RouteSnapshot?[] routes = [null, GoldenFixture.CopyRoute(source, [])];
        foreach (var route in routes)
        {
            var result = new TimeEngineV1().Calculate(new(route, 150));
            Assert.Equal(CalculationStatus.Blocked, result.Status);
            Assert.NotEmpty(result.Errors);
            Assert.Null(result.SumTjSecPerUnit);
            Assert.Null(result.SumTpzMinPerBatch);
            Assert.Null(result.SumTpzSecPerUnit);
            Assert.Null(result.LaborSecPerUnit);
            Assert.Null(result.LaborHoursBatch);
            Assert.Empty(result.OperationResults);
        }
    }

    [Fact]
    public void Duplicate_operation_number_is_rejected_by_domain()
    {
        var operations = EntityOperations();
        operations[1] = operations[1] with { OperationNo = operations[0].OperationNo };
        var error = Assert.Throws<DomainValidationException>(() => EntityRoute(operations));
        Assert.Contains(error.Errors, x => x.Code == "DUPLICATE_OPERATION_NO");
    }

    [Fact]
    public void Duplicate_sequence_is_rejected_by_domain()
    {
        var operations = EntityOperations();
        operations[1] = operations[1] with { SequenceNo = operations[0].SequenceNo };
        var error = Assert.Throws<DomainValidationException>(() => EntityRoute(operations));
        Assert.Contains(error.Errors, x => x.Code == "DUPLICATE_SEQUENCE_NO");
    }

    [Fact]
    public void Negative_tj_and_tpz_are_rejected_by_domain()
    {
        var operations = EntityOperations();
        operations[0] = operations[0] with { TjSec = -0.000001m, TpzMinPerBatch = -1m };
        var error = Assert.Throws<DomainValidationException>(() => EntityRoute(operations));
        Assert.Contains(error.Errors, x => x.Code == "TJ_NEGATIVE");
        Assert.Contains(error.Errors, x => x.Code == "TPZ_NEGATIVE");
    }

    [Fact]
    public void Pure_time_engine_rejects_negative_times_as_validation_error()
    {
        var source = GoldenFixture.Load().Route;
        var invalid = GoldenFixture.CopyOperation(source.Operations[0], tjSec: -1m, tpzMin: -1m);
        var error = Assert.Throws<DomainValidationException>(() =>
            new TimeEngineV1().Calculate(new(GoldenFixture.CopyRoute(source, [invalid]), 150)));
        Assert.Contains(error.Errors, x => x.Code == "TJ_NEGATIVE");
        Assert.Contains(error.Errors, x => x.Code == "TPZ_NEGATIVE");
    }

    [Fact]
    public void Missing_machine_and_times_cannot_be_approved()
    {
        var operations = EntityOperations();
        operations[0] = operations[0] with { Machine = null, TjSec = null, TpzMinPerBatch = null, ApprovalStatus = ApprovalStatus.Proposed };
        var draft = EntityRoute(operations, RouteStatus.Draft);
        var error = Assert.Throws<DomainValidationException>(() => draft.Approve());
        Assert.Contains(error.Errors, x => x.Code == "MACHINE_MISSING");
        Assert.Contains(error.Errors, x => x.Code == "TJ_MISSING");
        Assert.Contains(error.Errors, x => x.Code == "TPZ_MISSING");
        Assert.Contains(error.Errors, x => x.Code == "OPERATION_NOT_APPROVED");
        Assert.Equal(RouteStatus.Draft, draft.Status);
    }

    [Fact]
    public void Explicit_confirmed_zero_times_are_valid_but_missing_times_block()
    {
        var snapshot = GoldenFixture.Load();
        var zeroOperation = snapshot.Route.Operations[^1];
        var zeroResult = new TimeEngineV1().Calculate(new(GoldenFixture.CopyRoute(snapshot.Route, [zeroOperation]), 150));
        Assert.Equal(CalculationStatus.Success, zeroResult.Status);
        Assert.Equal(0m, zeroResult.LaborHoursBatch);
        var missing = new OperationSnapshot(zeroOperation.OperationNo, zeroOperation.SequenceNo, zeroOperation.Name,
            zeroOperation.Machine, null, null, zeroOperation.Origin, zeroOperation.ApprovalStatus, []);
        var result = new TimeEngineV1().Calculate(new(GoldenFixture.CopyRoute(snapshot.Route, [missing]), 150));
        Assert.Equal(CalculationStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, x => x.Code == "TJ_MISSING");
        Assert.Contains(result.Errors, x => x.Code == "TPZ_MISSING");
        Assert.Null(result.LaborHoursBatch);
    }

    [Fact]
    public void Approved_route_rejects_in_place_edit_and_requires_distinct_version()
    {
        var route = EntityRoute(EntityOperations());
        var error = Assert.Throws<DomainValidationException>(() => route.WithOperations([]));
        Assert.Contains(error.Errors, x => x.Code == "APPROVED_ROUTE_IMMUTABLE");
        Assert.Throws<NotSupportedException>(() => ((IList<ProcessOperation>)route.Operations).Clear());
        Assert.Throws<DomainValidationException>(() => route.CreateNewVersion(RouteId, "2", []));
        Assert.Throws<DomainValidationException>(() => route.CreateNewVersion(Guid.Empty, "1", []));
        Assert.Equal(8, route.Operations.Count);
        Assert.Equal(RouteStatus.Approved, route.Status);

        var newId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        var next = route.CreateNewVersion(newId, "2", route.Operations.Select(x => x with { RouteId = newId }));
        Assert.Equal(RouteStatus.Draft, next.Status);
        Assert.Equal("2", next.Version);
        Assert.Equal("1", route.Version);
        Assert.Equal(RouteStatus.Approved, next.Approve().Status);
    }

    [Fact]
    public void Draft_can_be_changed_and_approved_without_mutating_original()
    {
        var draft = EntityRoute(EntityOperations(), RouteStatus.Draft);
        var changed = draft.WithOperations(draft.Operations.Take(1));
        Assert.Equal(8, draft.Operations.Count);
        Assert.Single(changed.Operations);
        Assert.Equal(RouteStatus.Approved, changed.Approve().Status);
        Assert.Equal(RouteStatus.Draft, changed.Status);
    }

    [Fact]
    public void Empty_route_cannot_be_approved()
    {
        var draft = EntityRoute([], RouteStatus.Draft);
        var error = Assert.Throws<DomainValidationException>(() => draft.Approve());
        Assert.Contains(error.Errors, x => x.Code == "ROUTE_EMPTY");
    }

    [Fact]
    public void Cross_tenant_operation_or_machine_is_rejected()
    {
        var operations = EntityOperations();
        operations[0] = operations[0] with { Machine = operations[0].Machine! with { TenantId = Guid.Empty } };
        var error = Assert.Throws<DomainValidationException>(() => EntityRoute(operations));
        Assert.Contains(error.Errors, x => x.Code == "ROUTE_OWNERSHIP_MISMATCH");
    }

    private static ProcessRoute EntityRoute(IEnumerable<ProcessOperation> operations, RouteStatus status = RouteStatus.Approved) =>
        new(GoldenFixture.Load().TenantId, RouteId, RevisionId, "W07044", "1", status, operations);

    private static ProcessOperation[] EntityOperations()
    {
        var snapshot = GoldenFixture.Load();
        return snapshot.Route.Operations.Select(x => new ProcessOperation(snapshot.TenantId,
            Guid.Parse($"00000000-0000-4000-8000-{x.SequenceNo:D12}"), RouteId, x.OperationNo,
            x.SequenceNo, x.Name, new Machine(snapshot.TenantId,
                Guid.Parse($"00000001-0000-4000-8000-{x.SequenceNo:D12}"), x.Machine!.Code, x.Machine.Name),
            x.TjSec, x.TpzMinPerBatch, x.Origin, x.ApprovalStatus, x.SetupDescription, x.SourceReference)).ToArray();
    }
}
