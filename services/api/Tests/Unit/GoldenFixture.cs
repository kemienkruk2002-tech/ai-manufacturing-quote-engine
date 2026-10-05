using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

internal static class GoldenFixture
{
    public static QuoteSnapshotPayload Load() => CanonicalSnapshotSerializer.Deserialize(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "w07044.json")));

    public static OperationSnapshot CopyOperation(OperationSnapshot operation, string? operationNo = null,
        int? sequenceNo = null, decimal? tjSec = null, decimal? tpzMin = null,
        IReadOnlyList<InspectionSnapshot>? inspections = null) =>
        new(operationNo ?? operation.OperationNo, sequenceNo ?? operation.SequenceNo, operation.Name,
            operation.Machine, tjSec ?? operation.TjSec, tpzMin ?? operation.TpzMinPerBatch,
            operation.Origin, operation.ApprovalStatus, inspections ?? operation.InspectionRequirements,
            operation.SetupDescription, operation.SourceReference);

    public static RouteSnapshot CopyRoute(RouteSnapshot route, IReadOnlyList<OperationSnapshot> operations) =>
        new(route.RouteCode, route.Version, route.Status, operations, route.SourceReference);
}
