using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Domain.Quoting;

public enum QuoteStatus { New, DataReview, ReadyForCalc, Calculated, Approved, Sent, Lost, Won, Blocked }

public sealed record QuoteRequest(Guid TenantId, Guid Id, Guid? PartRevisionId, int? RequestedQuantity,
    QuoteStatus Status, string Currency = "PLN", Guid? CustomerId = null,
    string? ExternalRfqNo = null, DateOnly? RequestedDueDate = null);

public sealed record RfqFileVersionInput(Guid TenantId, Guid QuoteRequestId, string LogicalKey,
    string OriginalFileName, string? MimeType, long ByteSize, string Sha256,
    string? SourceReference = null);

public sealed record RfqFileVersion(Guid TenantId, Guid Id, Guid QuoteRequestId, Guid DocumentId,
    string LogicalKey, int VersionNo, string OriginalFileName, string? MimeType,
    long ByteSize, string Sha256, DateTimeOffset CreatedAt, string? SourceReference = null);

public sealed class QuoteSnapshot
{
    public Guid TenantId { get; }
    public Guid Id { get; }
    public Guid QuoteRequestId { get; }
    public QuoteSnapshotPayload Payload { get; }
    public string CanonicalJson { get; }
    public string SnapshotHash { get; }
    public DateTimeOffset CreatedAt { get; }

    public QuoteSnapshot(Guid tenantId, Guid id, Guid quoteRequestId, QuoteSnapshotPayload payload,
        DateTimeOffset createdAt)
    {
        if (tenantId != payload.TenantId)
            throw new DomainValidationException("SNAPSHOT_TENANT_MISMATCH", "Snapshot and payload must belong to the same tenant.");
        if (payload.Quantity <= 0)
            throw new DomainValidationException("QUANTITY_INVALID", "Quantity must be greater than zero.");
        TenantId = tenantId;
        Id = id;
        QuoteRequestId = quoteRequestId;
        Payload = payload;
        CreatedAt = createdAt.ToUniversalTime();
        CanonicalJson = CanonicalSnapshotDispatcher.Serialize(payload);
        SnapshotHash = CanonicalSnapshotDispatcher.ComputeSnapshotHash(payload);
    }
}

public sealed record CalculationRun(Guid TenantId, Guid Id, Guid QuoteSnapshotId, string EngineVersion,
    string TimeEngineVersion, string StockEngineVersion, string CalculationHash,
    CalculationStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? ErrorCode = null);

public sealed record CalculationOperationResult(Guid TenantId, Guid Id, Guid CalculationRunId,
    Guid ProcessOperationId, int Quantity, decimal UnitTjSec, decimal BatchTpzMin,
    decimal UnitTpzSec, decimal UnitLaborSec);
