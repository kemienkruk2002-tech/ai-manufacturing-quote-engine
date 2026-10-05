using System.Text.Json.Serialization;
using QuoteEngine.Domain.Materials;
using QuoteEngine.Domain.Parts;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Domain.Quoting;

public sealed record PartRevisionSnapshot(string PartNumber, string Name, string? RevisionCode,
    string? Variant, string? ProcessVersion, PartRevisionStatus Status, decimal? FinalMassKg,
    string? SourceReference = null);

public sealed record MaterialSnapshot(string Code, string Grade, string DisplayName,
    string? SourceReference = null);

public sealed record StockSnapshot(StockType Type, decimal? DiameterMm, decimal? NormMassKgPerUnit,
    StockSourceType SourceType, ApprovalStatus ApprovalStatus, string? SourceReference = null,
    string? RuleVersion = null);

public sealed record MachineSnapshot(string Code, string Name);

// Keep the source's protocol text (including "Częściowo"); do not infer a boolean.
public sealed record InspectionSnapshot(string RequirementNo, string RequirementType,
    string Description, string? MeasurementMethod, string? SamplingText, string? ProtocolRequirementText,
    InspectionOrigin Origin, ApprovalStatus ApprovalStatus, string? SourceReference = null);

public sealed record OperationSnapshot
{
    public string OperationNo { get; }
    public int SequenceNo { get; }
    public string Name { get; }
    public MachineSnapshot? Machine { get; }
    public decimal? TjSec { get; }
    public decimal? TpzMinPerBatch { get; }
    public OperationOrigin Origin { get; }
    public ApprovalStatus ApprovalStatus { get; }
    public string? SetupDescription { get; }
    public string? SourceReference { get; }
    public IReadOnlyList<InspectionSnapshot> InspectionRequirements { get; }

    [JsonConstructor]
    public OperationSnapshot(string operationNo, int sequenceNo, string name, MachineSnapshot? machine,
        decimal? tjSec, decimal? tpzMinPerBatch, OperationOrigin origin, ApprovalStatus approvalStatus,
        IReadOnlyList<InspectionSnapshot> inspectionRequirements, string? setupDescription = null,
        string? sourceReference = null)
    {
        OperationNo = operationNo;
        SequenceNo = sequenceNo;
        Name = name;
        Machine = machine;
        TjSec = tjSec;
        TpzMinPerBatch = tpzMinPerBatch;
        Origin = origin;
        ApprovalStatus = approvalStatus;
        SetupDescription = setupDescription;
        SourceReference = sourceReference;
        InspectionRequirements = Array.AsReadOnly(inspectionRequirements
            .OrderBy(x => x.RequirementNo, StringComparer.Ordinal)
            .ThenBy(x => x.RequirementType, StringComparer.Ordinal)
            .ThenBy(x => x.Description, StringComparer.Ordinal)
            .ThenBy(x => x.MeasurementMethod, StringComparer.Ordinal)
            .ThenBy(x => x.SamplingText, StringComparer.Ordinal)
            .ThenBy(x => x.ProtocolRequirementText, StringComparer.Ordinal)
            .ThenBy(x => x.Origin).ThenBy(x => x.ApprovalStatus)
            .ThenBy(x => x.SourceReference, StringComparer.Ordinal).ToArray());
    }
}

public sealed record RouteSnapshot
{
    public string RouteCode { get; }
    public string Version { get; }
    public RouteStatus Status { get; }
    public string? SourceReference { get; }
    public IReadOnlyList<OperationSnapshot> Operations { get; }

    [JsonConstructor]
    public RouteSnapshot(string routeCode, string version, RouteStatus status,
        IReadOnlyList<OperationSnapshot> operations, string? sourceReference = null)
    {
        RouteCode = routeCode;
        Version = version;
        Status = status;
        SourceReference = sourceReference;
        Operations = Array.AsReadOnly(operations.OrderBy(x => x.SequenceNo)
            .ThenBy(x => x.OperationNo, StringComparer.Ordinal).ToArray());
    }
}

// Rates are deliberately unavailable in stage 1; this is an explicit version reference.
public sealed record MachineRatesReference(string Version, string Status);
public sealed record RuleVersions(string TimeEngine, string StockEngine, string CanonicalSchema);

public sealed record OperationCostRateSnapshot(string OperationNo, string MachineCode,
    decimal? RateTpzPlnH, decimal? RateProductionPlnH, decimal? RateOverallPlnH,
    string? SourceReference = null);

public sealed record CostInputsSnapshot
{
    public decimal? MaterialCostUnit { get; }
    public string Currency { get; }
    public string MachineRateVersion { get; }
    public string CostEngineVersion { get; }
    public string? MaterialCostSourceReference { get; }
    public IReadOnlyList<OperationCostRateSnapshot> OperationRates { get; }

    [JsonConstructor]
    public CostInputsSnapshot(decimal? materialCostUnit, string currency, string machineRateVersion,
        string costEngineVersion, IReadOnlyList<OperationCostRateSnapshot> operationRates,
        string? materialCostSourceReference = null)
    {
        MaterialCostUnit = materialCostUnit;
        Currency = currency;
        MachineRateVersion = machineRateVersion;
        CostEngineVersion = costEngineVersion;
        MaterialCostSourceReference = materialCostSourceReference;
        OperationRates = Array.AsReadOnly(operationRates.OrderBy(x => x.OperationNo, StringComparer.Ordinal)
            .ThenBy(x => x.MachineCode, StringComparer.Ordinal).ToArray());
    }
}

public sealed record QuoteSnapshotPayload(Guid TenantId, PartRevisionSnapshot PartRevision,
    MaterialSnapshot Material, StockSnapshot Stock, RouteSnapshot Route, int Quantity,
    MachineRatesReference MachineRates, RuleVersions RuleVersions, CostInputsSnapshot? Cost = null);
