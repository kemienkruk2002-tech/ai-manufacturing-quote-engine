using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Machines;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Domain.Routing;

public enum RouteStatus { Draft, Approved, Archived }
public enum OperationOrigin { Manual, Document, AiProposed, Imported }
public enum InspectionOrigin { Document, AiExtracted, Manual }
public enum ApprovalStatus { Proposed, Approved, Rejected }

public sealed record InspectionRequirement(Guid TenantId, Guid Id, Guid ProcessOperationId,
    string RequirementNo, string RequirementType, string Description, string? MeasurementMethod,
    string? SamplingText, string? ProtocolRequirementText, InspectionOrigin Origin,
    ApprovalStatus ApprovalStatus, string? SourceReference = null);

public sealed record ProcessOperation(Guid TenantId, Guid Id, Guid RouteId, string OperationNo,
    int SequenceNo, string Name, Machine? Machine, decimal? TjSec, decimal? TpzMinPerBatch,
    OperationOrigin Origin, ApprovalStatus ApprovalStatus, string? SetupDescription = null,
    string? SourceReference = null)
{
    public OperationSnapshot ToSnapshot() => new(OperationNo, SequenceNo, Name,
        Machine is null ? null : new MachineSnapshot(Machine.Code, Machine.Name),
        TjSec, TpzMinPerBatch, Origin, ApprovalStatus, [], SetupDescription, SourceReference);
}

public sealed class ProcessRoute
{
    public Guid TenantId { get; }
    public Guid Id { get; }
    public Guid PartRevisionId { get; }
    public string RouteCode { get; }
    public string Version { get; }
    public RouteStatus Status { get; }
    public IReadOnlyList<ProcessOperation> Operations { get; }

    public ProcessRoute(Guid tenantId, Guid id, Guid partRevisionId, string routeCode, string version,
        RouteStatus status, IEnumerable<ProcessOperation> operations)
    {
        TenantId = tenantId;
        Id = id;
        PartRevisionId = partRevisionId;
        RouteCode = routeCode;
        Version = version;
        Status = status;
        Operations = Array.AsReadOnly(operations.OrderBy(x => x.SequenceNo).ToArray());
        if (Operations.Any(x => x.TenantId != tenantId || x.RouteId != id
            || (x.Machine is not null && x.Machine.TenantId != tenantId)))
            throw new DomainValidationException("ROUTE_OWNERSHIP_MISMATCH", "Operations and machines must belong to the route's tenant and route.");
        var errors = RouteValidator.Validate(ToSnapshot(), requireApproved: status == RouteStatus.Approved);
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }

    public RouteSnapshot ToSnapshot() => new(RouteCode, Version, Status, Operations.Select(x => x.ToSnapshot()).ToArray());

    public ProcessRoute Approve()
    {
        if (Status != RouteStatus.Draft)
            throw new DomainValidationException("ROUTE_NOT_DRAFT", "Only a draft route can be approved.");
        return new(TenantId, Id, PartRevisionId, RouteCode, Version, RouteStatus.Approved, Operations);
    }

    public ProcessRoute WithOperations(IEnumerable<ProcessOperation> operations)
    {
        if (Status != RouteStatus.Draft)
            throw new DomainValidationException("APPROVED_ROUTE_IMMUTABLE", "An approved or archived route requires a new version.");
        return new(TenantId, Id, PartRevisionId, RouteCode, Version, Status, operations);
    }

    public ProcessRoute CreateNewVersion(Guid newRouteId, string newVersion, IEnumerable<ProcessOperation> operations)
    {
        if (newRouteId == Id || string.IsNullOrWhiteSpace(newVersion) || newVersion == Version)
            throw new DomainValidationException("ROUTE_VERSION_UNCHANGED", "A new route must have a distinct identity and version.");
        return new(TenantId, newRouteId, PartRevisionId, RouteCode, newVersion, RouteStatus.Draft, operations);
    }
}

public static class RouteValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(RouteSnapshot? route, bool requireApproved = true)
    {
        var errors = new List<ValidationIssue>();
        if (route is null) return Array.AsReadOnly(new[] { new ValidationIssue("ROUTE_MISSING", "A route is required.") });
        if (string.IsNullOrWhiteSpace(route.RouteCode) || string.IsNullOrWhiteSpace(route.Version))
            errors.Add(new("ROUTE_IDENTITY_MISSING", "The route code and version are required."));
        if (requireApproved && route.Status != RouteStatus.Approved)
            errors.Add(new("ROUTE_NOT_APPROVED", "A calculation requires an approved route."));
        if (route.Operations.Count == 0 && requireApproved)
            errors.Add(new("ROUTE_EMPTY", "A calculation requires at least one operation."));
        foreach (var group in route.Operations.GroupBy(x => x.OperationNo, StringComparer.Ordinal).Where(x => x.Count() > 1))
            errors.Add(new("DUPLICATE_OPERATION_NO", "Operation numbers must be unique within a route.", group.Key));
        foreach (var group in route.Operations.GroupBy(x => x.SequenceNo).Where(x => x.Count() > 1))
            errors.Add(new("DUPLICATE_SEQUENCE_NO", $"Sequence {group.Key} occurs more than once."));
        foreach (var operation in route.Operations)
        {
            void Add(string code, string message) => errors.Add(new(code, message, operation.OperationNo));
            if (string.IsNullOrWhiteSpace(operation.OperationNo)) Add("OPERATION_NO_MISSING", "Operation number is required.");
            if (operation.SequenceNo <= 0) Add("SEQUENCE_NO_INVALID", "Operation sequence must be positive.");
            if (operation.TjSec < 0) Add("TJ_NEGATIVE", "Tj cannot be negative.");
            if (operation.TpzMinPerBatch < 0) Add("TPZ_NEGATIVE", "Tpz cannot be negative.");
            if (requireApproved || operation.ApprovalStatus == ApprovalStatus.Approved)
            {
                if (operation.ApprovalStatus != ApprovalStatus.Approved) Add("OPERATION_NOT_APPROVED", "Every operation must be approved.");
                if (operation.Machine is null || string.IsNullOrWhiteSpace(operation.Machine.Code)) Add("MACHINE_MISSING", "An approved operation must have a machine.");
                if (operation.TjSec is null) Add("TJ_MISSING", "Tj must be supplied explicitly, including a confirmed zero.");
                if (operation.TpzMinPerBatch is null) Add("TPZ_MISSING", "Tpz must be supplied explicitly, including a confirmed zero.");
            }
        }
        return Array.AsReadOnly(errors.ToArray());
    }
}
