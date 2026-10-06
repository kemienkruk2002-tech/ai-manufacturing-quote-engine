using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Customers;
using QuoteEngine.Domain.Machines;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application;

public sealed record PartSnapshotData(PartRevisionSnapshot Revision, MaterialSnapshot Material, StockSnapshot Stock);
public sealed record SnapshotRequest(Guid TenantId, Guid QuoteRequestId, string RouteCode, string RouteVersion,
    string MachineRateVersion, RuleVersions RuleVersions);
public sealed record PreparedSnapshot(QuoteSnapshotPayload Payload, Guid SourceRouteId, Guid SourceRevisionId);
public sealed record StoredSnapshot(Guid Id, Guid TenantId, Guid QuoteRequestId, Guid SourceRouteId, string CanonicalJson, string SnapshotHash);
public sealed record StoredCalculation(Guid Id, Guid TenantId, Guid SnapshotId, string CalculationHash,
    string EngineVersion, string ResultJson, CalculationStatus Status);
public sealed record CalculationWrite(StoredSnapshot Snapshot, QuoteSnapshotPayload Payload, string EngineVersion,
    string CalculationHash, TimeCalculationResult TimeResult, StockCalculationResult StockResult,
    CostCalculationResult? CostResult, string ResultJson, string CorrelationId,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, decimal DurationMs);
public sealed record QuoteRequestFilter(QuoteStatus? Status = null, Guid? CustomerId = null,
    DateOnly? DueFrom = null, DateOnly? DueTo = null);
public sealed record QuoteDraftUpdate(Guid? CustomerId, Guid? PartRevisionId, int? RequestedQuantity,
    string Currency, string? ExternalRfqNo, DateOnly? RequestedDueDate, long ExpectedRowVersion);
public sealed record RfqFileManifestDocument(Guid DocumentId, string LogicalKey, IReadOnlyList<RfqFileVersion> Versions);
public sealed record RfqExtractionExecutionWrite(Guid TenantId, Guid QuoteRequestId,
    string ModelId, string PromptVersion, string SchemaVersion, string? RequestFingerprint,
    AiExecutionDisposition Disposition, string? Code);
public sealed record StoredRfqExtractionExecution(Guid AuditEventId, Guid TenantId, Guid QuoteRequestId,
    string ModelId, string PromptVersion, string SchemaVersion, string? RequestFingerprint,
    AiExecutionDisposition Disposition, string? Code, DateTimeOffset CreatedAt);
public sealed record RfqExtractionAttemptWrite(Guid TenantId, Guid QuoteRequestId,
    string ModelId, string PromptVersion, string SchemaVersion, string? RequestFingerprint,
    AiExecutionDisposition Disposition, string? Code, string SourceLineageJson,
    string? RawProviderOutput, string? CanonicalDraftJson);
public sealed record StoredRfqExtractionAttempt(Guid Id, Guid TenantId, Guid QuoteRequestId,
    string ModelId, string PromptVersion, string SchemaVersion, string? RequestFingerprint,
    AiExecutionDisposition Disposition, string? Code, string SourceLineageJson,
    string? RawProviderOutput, DateTimeOffset CreatedAt);
public sealed record StoredRfqCanonicalDraft(Guid TenantId, Guid QuoteRequestId, Guid SourceAttemptId,
    string CanonicalJson, long RowVersion, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record RfqExtractionPersistenceResult(StoredRfqExtractionAttempt Attempt,
    StoredRfqCanonicalDraft? CurrentDraft);

public interface ICustomerRepository
{
    Task<Customer> CreateAsync(Customer customer, CancellationToken cancellationToken = default);
    Task<Customer?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken = default);
    Task<CustomerContact> CreateContactAsync(CustomerContact contact, CancellationToken cancellationToken = default);
    Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerContact>> ListContactsAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken = default);
}
public interface IQuoteRequestRepository
{
    Task<QuoteRequest> CreateAsync(QuoteRequest request, CancellationToken cancellationToken = default);
    Task<QuoteRequest?> FindAsync(Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuoteRequest>> ListAsync(Guid tenantId, QuoteRequestFilter filter, CancellationToken cancellationToken = default);
    Task<QuoteRequest?> UpdateDraftAsync(Guid tenantId, Guid quoteRequestId, QuoteDraftUpdate update,
        CancellationToken cancellationToken = default);
}
public interface IPartRepository { Task<PartSnapshotData> GetRevisionAsync(Guid tenantId, Guid revisionId, CancellationToken cancellationToken = default); }
public interface IRouteRepository { Task<RouteSnapshot> GetApprovedAsync(Guid tenantId, Guid revisionId, string routeCode, string version, CancellationToken cancellationToken = default); }
public interface IMachineRateRepository { Task<decimal?> FindRateAsync(Guid tenantId, Guid machineId, RateType rateType, DateTimeOffset effectiveAt, string rateVersion, CancellationToken cancellationToken = default); }
public interface ISnapshotInputRepository { Task<PreparedSnapshot> BuildAsync(SnapshotRequest request, CancellationToken cancellationToken = default); }
public interface IQuoteSnapshotRepository
{
    Task<StoredSnapshot> GetOrCreateAsync(Guid quoteRequestId, PreparedSnapshot prepared, CancellationToken cancellationToken = default);
    Task<StoredSnapshot?> FindAsync(Guid tenantId, string snapshotHash, CancellationToken cancellationToken = default);
}
public interface ICalculationRunRepository
{
    Task<StoredCalculation> SaveAsync(CalculationWrite calculation, CancellationToken cancellationToken = default);
    Task<StoredCalculation?> FindAsync(Guid tenantId, string calculationHash, CancellationToken cancellationToken = default);
}
public interface IRfqFileRepository
{
    Task<bool> RequestExistsAsync(Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default);
    Task<RfqFileVersion> GetOrCreateVersionAsync(RfqFileVersionInput input, CancellationToken cancellationToken = default);
    Task<RfqFileVersion?> FindVersionAsync(Guid tenantId, Guid quoteRequestId, string logicalKey, int versionNo, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RfqFileVersion>> ListVersionsAsync(Guid tenantId, Guid quoteRequestId, string logicalKey, CancellationToken cancellationToken = default);
}
public interface IRfqFileManifestRepository
{
    Task<IReadOnlyList<RfqFileManifestDocument>> ListAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default);
}
public interface IRfqExtractionExecutionRepository
{
    Task<StoredRfqExtractionExecution> SaveAsync(RfqExtractionExecutionWrite write,
        CancellationToken cancellationToken = default);
    Task<RfqExtractionPersistenceResult> SaveAttemptAsync(RfqExtractionAttemptWrite write,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UnixEpoch;
        var attempt = new StoredRfqExtractionAttempt(Guid.Empty, write.TenantId, write.QuoteRequestId,
            write.ModelId, write.PromptVersion, write.SchemaVersion, write.RequestFingerprint,
            write.Disposition, write.Code, write.SourceLineageJson, write.RawProviderOutput, now);
        StoredRfqCanonicalDraft? draft = write.CanonicalDraftJson is null ? null
            : new(write.TenantId, write.QuoteRequestId, Guid.Empty, write.CanonicalDraftJson, 1, now, now);
        return Task.FromResult(new RfqExtractionPersistenceResult(attempt, draft));
    }
    Task<IReadOnlyList<StoredRfqExtractionAttempt>> ListAttemptsAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredRfqExtractionAttempt>>(Array.Empty<StoredRfqExtractionAttempt>());
    Task<StoredRfqCanonicalDraft?> FindCurrentDraftAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default) => Task.FromResult<StoredRfqCanonicalDraft?>(null);
}
public sealed record FileObjectPutResult(string Sha256, long ByteSize, bool Created);
public interface IFileObjectStore
{
    Task<FileObjectPutResult> PutAsync(Stream content, string expectedSha256, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default);
}
public sealed class FileObjectHashMismatchException(string expectedSha256, string actualSha256)
    : Exception($"FILE_OBJECT_HASH_MISMATCH: expected {expectedSha256}, calculated {actualSha256}.")
{
    public string ExpectedSha256 { get; } = expectedSha256;
    public string ActualSha256 { get; } = actualSha256;
}
public sealed class MissingSnapshotInputException(string code, string message) : Exception(message) { public string Code { get; } = code; }