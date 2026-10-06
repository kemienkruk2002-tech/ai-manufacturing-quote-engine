using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class RfqExtractionPersistenceRepository(NpgsqlDataSource dataSource)
    : IRfqExtractionPersistenceRepository
{
    public async Task<RfqExtractionPersistenceResult> SaveAsync(
        RfqExtractionAttemptWrite write,
        CancellationToken cancellationToken = default)
    {
        Validate(write);
        var attemptId = Guid.NewGuid();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var attemptCommand = new NpgsqlCommand("""
            INSERT INTO rfq_extraction_attempts(
                id,tenant_id,quote_request_id,request_fingerprint,model_id,prompt_version,schema_version,
                disposition,result_code,source_lineage,raw_provider_output)
            SELECT @id,q.tenant_id,q.id,@fingerprint,@model,@prompt,@schema,@disposition,@code,
                CAST(@lineage AS jsonb),@raw
            FROM quote_requests q
            WHERE q.tenant_id=@tenant AND q.id=@rfq
            RETURNING created_at
            """, connection, transaction);
        attemptCommand.Parameters.AddWithValue("id", attemptId);
        attemptCommand.Parameters.AddWithValue("tenant", write.TenantId);
        attemptCommand.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        attemptCommand.Parameters.AddWithValue("fingerprint", (object?)write.RequestFingerprint ?? DBNull.Value);
        attemptCommand.Parameters.AddWithValue("model", write.ModelId);
        attemptCommand.Parameters.AddWithValue("prompt", write.PromptVersion);
        attemptCommand.Parameters.AddWithValue("schema", write.SchemaVersion);
        attemptCommand.Parameters.AddWithValue("disposition", write.Disposition.ToString());
        attemptCommand.Parameters.AddWithValue("code", (object?)write.Code ?? DBNull.Value);
        attemptCommand.Parameters.AddWithValue("lineage", write.SourceLineageJson);
        attemptCommand.Parameters.AddWithValue("raw", (object?)write.RawProviderOutput ?? DBNull.Value);
        var createdValue = await attemptCommand.ExecuteScalarAsync(cancellationToken);
        if (createdValue is null)
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_RFQ_NOT_FOUND",
                "The RFQ does not exist in the requested tenant.");
        var createdAt = (DateTimeOffset)createdValue;

        StoredRfqCanonicalDraft? draft = null;
        if (write.Disposition == AiExecutionDisposition.COMPLETED && write.CanonicalDraftJson is not null)
        {
            await using var draftCommand = new NpgsqlCommand("""
                INSERT INTO rfq_canonical_drafts(
                    tenant_id,quote_request_id,source_attempt_id,canonical_json,row_version)
                VALUES (@tenant,@rfq,@attempt,CAST(@canonical AS jsonb),1)
                ON CONFLICT (tenant_id,quote_request_id) DO UPDATE SET
                    source_attempt_id=EXCLUDED.source_attempt_id,
                    canonical_json=EXCLUDED.canonical_json,
                    row_version=rfq_canonical_drafts.row_version+1,
                    updated_at=now()
                RETURNING source_attempt_id,canonical_json::text,row_version,created_at,updated_at
                """, connection, transaction);
            draftCommand.Parameters.AddWithValue("tenant", write.TenantId);
            draftCommand.Parameters.AddWithValue("rfq", write.QuoteRequestId);
            draftCommand.Parameters.AddWithValue("attempt", attemptId);
            draftCommand.Parameters.AddWithValue("canonical", write.CanonicalDraftJson);
            await using var reader = await draftCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Canonical RFQ draft upsert returned no row.");
            draft = new(write.TenantId, write.QuoteRequestId, reader.GetGuid(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4));
        }

        await transaction.CommitAsync(cancellationToken);
        var attempt = new StoredRfqExtractionAttempt(attemptId, write.TenantId, write.QuoteRequestId,
            write.ModelId, write.PromptVersion, write.SchemaVersion, write.RequestFingerprint,
            write.Disposition, write.Code, write.SourceLineageJson, write.RawProviderOutput, createdAt);
        return new(attempt, draft);
    }

    public async Task<IReadOnlyList<StoredRfqExtractionAttempt>> ListAttemptsAsync(
        Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, quoteRequestId);
        await using var command = dataSource.CreateCommand("""
            SELECT id,model_id,prompt_version,schema_version,request_fingerprint,disposition,result_code,
                   source_lineage::text,raw_provider_output,created_at
            FROM rfq_extraction_attempts
            WHERE tenant_id=@tenant AND quote_request_id=@rfq
            ORDER BY created_at,id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<StoredRfqExtractionAttempt>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new(reader.GetGuid(0), tenantId, quoteRequestId, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                Enum.Parse<AiExecutionDisposition>(reader.GetString(5), false),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return results;
    }

    public async Task<StoredRfqCanonicalDraft?> FindCurrentDraftAsync(
        Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, quoteRequestId);
        await using var command = dataSource.CreateCommand("""
            SELECT source_attempt_id,canonical_json::text,row_version,created_at,updated_at
            FROM rfq_canonical_drafts
            WHERE tenant_id=@tenant AND quote_request_id=@rfq
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(tenantId, quoteRequestId, reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static void Validate(RfqExtractionAttemptWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateIdentity(write.TenantId, write.QuoteRequestId);
        if (string.IsNullOrWhiteSpace(write.ModelId) || string.IsNullOrWhiteSpace(write.PromptVersion)
            || string.IsNullOrWhiteSpace(write.SchemaVersion))
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_VERSION_INVALID",
                "Model, prompt and schema versions are required.");
        if (write.RequestFingerprint is not null
            && (write.RequestFingerprint.Length != 64 || write.RequestFingerprint.Any(c =>
                c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_FINGERPRINT_INVALID",
                "Request fingerprint must be null or lowercase 64-hex.");
        if (write.Code is not null && string.IsNullOrWhiteSpace(write.Code))
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_CODE_INVALID",
                "Result code must be null or non-empty.");
        ValidateJson(write.SourceLineageJson, JsonValueKind.Array, "RFQ_EXTRACTION_HISTORY_LINEAGE_INVALID");
        if (write.CanonicalDraftJson is not null)
            ValidateJson(write.CanonicalDraftJson, JsonValueKind.Object, "RFQ_EXTRACTION_DRAFT_INVALID");
        if (write.Disposition != AiExecutionDisposition.COMPLETED && write.CanonicalDraftJson is not null)
            throw new DomainValidationException("RFQ_EXTRACTION_DRAFT_DISPOSITION_INVALID",
                "Only a completed validated extraction may initialize the current draft.");
    }

    private static void ValidateJson(string json, JsonValueKind expected, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != expected) throw new JsonException();
        }
        catch (JsonException)
        {
            throw new DomainValidationException(code, "Persisted extraction JSON has an invalid shape.");
        }
    }

    private static void ValidateIdentity(Guid tenantId, Guid quoteRequestId)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty)
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required.");
    }
}
