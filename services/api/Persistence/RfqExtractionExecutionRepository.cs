using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class RfqExtractionExecutionRepository(NpgsqlDataSource dataSource)
    : IRfqExtractionExecutionRepository
{
    public const string Action = "RFQ_AI_EXTRACTION_EXECUTION";
    public const string Source = "RfqExtractionServiceV1";

    public async Task<StoredRfqExtractionExecution> SaveAsync(
        RfqExtractionExecutionWrite write,
        CancellationToken cancellationToken = default)
    {
        Validate(write);

        var payload = JsonSerializer.Serialize(new
        {
            request_fingerprint = write.RequestFingerprint,
            prompt_version = write.PromptVersion,
            schema_version = write.SchemaVersion,
            model_id = write.ModelId,
            disposition = write.Disposition.ToString(),
            code = write.Code
        });

        await using var command = dataSource.CreateCommand("""
            INSERT INTO audit_events(
                tenant_id,entity_type,entity_id,action,new_value,source
            )
            VALUES(
                @tenant,'quote_requests',@rfq,@action,CAST(@payload AS jsonb),@source
            )
            RETURNING id,created_at
            """);
        command.Parameters.AddWithValue("tenant", write.TenantId);
        command.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        command.Parameters.AddWithValue("action", Action);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("source", Source);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("RFQ extraction audit insert did not return a row.");

        return new(
            reader.GetGuid(0),
            write.TenantId,
            write.QuoteRequestId,
            write.ModelId,
            write.PromptVersion,
            write.SchemaVersion,
            write.RequestFingerprint,
            write.Disposition,
            write.Code,
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static void Validate(RfqExtractionExecutionWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.TenantId == Guid.Empty || write.QuoteRequestId == Guid.Empty)
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required.");
        if (string.IsNullOrWhiteSpace(write.ModelId)
            || string.IsNullOrWhiteSpace(write.PromptVersion)
            || string.IsNullOrWhiteSpace(write.SchemaVersion))
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_VERSION_INVALID",
                "Model, prompt and schema versions are required.");
        if (write.RequestFingerprint is not null
            && (write.RequestFingerprint.Length != 64
                || write.RequestFingerprint.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_FINGERPRINT_INVALID",
                "Request fingerprint must be null or lowercase 64-hex.");
        if (write.Code is not null && string.IsNullOrWhiteSpace(write.Code))
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_CODE_INVALID",
                "Result code must be null or non-empty.");
    }
}
