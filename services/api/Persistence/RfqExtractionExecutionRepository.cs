using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class RfqExtractionExecutionRepository(NpgsqlDataSource dataSource)
    : IRfqExtractionExecutionRepository
{
    public const string Action = "RFQ_AI_EXTRACTION_EXECUTION";
    public const string Source = "RfqExtractionServiceV1";

    public Task<RfqExtractionAtomicPersistenceResult> SaveAtomicAsync(
        RfqExtractionExecutionWrite execution,
        RfqExtractionAttemptWrite attempt,
        CancellationToken cancellationToken = default) =>
        SaveAtomicCoreAsync(execution, attempt, null, cancellationToken);

    public Task<RfqExtractionAtomicPersistenceResult> SaveKeyedAtomicAsync(
        RfqExtractionExecutionWrite execution,
        RfqExtractionAttemptWrite attempt,
        RfqExtractionIdempotencyIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyedIdentity(identity);
        return SaveAtomicCoreAsync(execution, attempt, identity, cancellationToken);
    }

    public async Task<RfqExtractionAtomicPersistenceResult?> FindKeyedAsync(
        Guid tenantId, Guid quoteRequestId, RfqExtractionIdempotencyIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, quoteRequestId);
        ValidateKeyedIdentity(identity);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await FindKeyedCoreAsync(connection, null, tenantId, quoteRequestId, identity, cancellationToken);
    }

    private async Task<RfqExtractionAtomicPersistenceResult> SaveAtomicCoreAsync(
        RfqExtractionExecutionWrite execution,
        RfqExtractionAttemptWrite attempt,
        RfqExtractionIdempotencyIdentity? identity,
        CancellationToken cancellationToken)
    {
        ValidateAudit(execution);
        ValidateAttempt(attempt);
        ValidateAtomicConsistency(execution, attempt);

        var auditPayload = JsonSerializer.Serialize(new
        {
            request_fingerprint = execution.RequestFingerprint,
            prompt_version = execution.PromptVersion,
            schema_version = execution.SchemaVersion,
            model_id = execution.ModelId,
            disposition = execution.Disposition.ToString(),
            code = execution.Code
        });

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (identity is not null)
        {
            // Transaction-scoped, process-independent PostgreSQL advisory lock. 64-bit
            // hash collisions only serialize unrelated scopes; SQL equality/uniqueness
            // still decides identity. Cancellation/rollback releases the lock.
            await LockKeyedScopeAsync(connection, transaction, execution.TenantId,
                execution.QuoteRequestId, identity, cancellationToken);
            var existing = await FindKeyedCoreAsync(connection, transaction, execution.TenantId,
                execution.QuoteRequestId, identity, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }

        Guid auditId;
        DateTimeOffset auditCreatedAt;
        await using (var auditCommand = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,entity_type,entity_id,action,new_value,source)
            SELECT q.tenant_id,'quote_requests',q.id,@action,CAST(@payload AS jsonb),@source
            FROM quote_requests q
            WHERE q.tenant_id=@tenant AND q.id=@rfq
            RETURNING id,created_at
            """, connection, transaction))
        {
            auditCommand.Parameters.AddWithValue("tenant", execution.TenantId);
            auditCommand.Parameters.AddWithValue("rfq", execution.QuoteRequestId);
            auditCommand.Parameters.AddWithValue("action", Action);
            auditCommand.Parameters.AddWithValue("payload", auditPayload);
            auditCommand.Parameters.AddWithValue("source", Source);
            await using var reader = await auditCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_RFQ_NOT_FOUND",
                    "The RFQ does not exist in the requested tenant.");
            auditId = reader.GetGuid(0);
            auditCreatedAt = ReadDateTimeOffset(reader, 1);
        }

        var attemptId = Guid.NewGuid();
        DateTimeOffset attemptCreatedAt;
        await using (var attemptCommand = new NpgsqlCommand("""
            INSERT INTO rfq_extraction_attempts(
                id,tenant_id,quote_request_id,request_fingerprint,model_id,prompt_version,schema_version,
                disposition,result_code,source_lineage,raw_provider_output,
                idempotency_version,idempotency_key,idempotency_request_hash,audit_event_id)
            VALUES(
                @id,@tenant,@rfq,@fingerprint,@model,@prompt,@schema,@disposition,@code,
                CAST(@lineage AS jsonb),@raw,@version,@key,@requestHash,@auditId)
            RETURNING created_at
            """, connection, transaction))
        {
            attemptCommand.Parameters.AddWithValue("id", attemptId);
            attemptCommand.Parameters.AddWithValue("tenant", attempt.TenantId);
            attemptCommand.Parameters.AddWithValue("rfq", attempt.QuoteRequestId);
            attemptCommand.Parameters.AddWithValue("fingerprint", (object?)attempt.RequestFingerprint ?? DBNull.Value);
            attemptCommand.Parameters.AddWithValue("model", attempt.ModelId);
            attemptCommand.Parameters.AddWithValue("prompt", attempt.PromptVersion);
            attemptCommand.Parameters.AddWithValue("schema", attempt.SchemaVersion);
            attemptCommand.Parameters.AddWithValue("disposition", attempt.Disposition.ToString());
            attemptCommand.Parameters.AddWithValue("code", (object?)attempt.Code ?? DBNull.Value);
            attemptCommand.Parameters.AddWithValue("lineage", attempt.SourceLineageJson);
            attemptCommand.Parameters.AddWithValue("raw", (object?)attempt.RawProviderOutput ?? DBNull.Value);
            attemptCommand.Parameters.Add("version", NpgsqlDbType.Text).Value =
                (object?)identity?.Version ?? DBNull.Value;
            attemptCommand.Parameters.Add("key", NpgsqlDbType.Text).Value =
                (object?)identity?.Key ?? DBNull.Value;
            attemptCommand.Parameters.Add("requestHash", NpgsqlDbType.Text).Value =
                (object?)identity?.RequestHash ?? DBNull.Value;
            attemptCommand.Parameters.Add("auditId", NpgsqlDbType.Uuid).Value =
                identity is null ? DBNull.Value : auditId;
            attemptCreatedAt = ToDateTimeOffset(
                await attemptCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Extraction attempt insert returned no created_at."));
        }

        StoredRfqCanonicalDraft? draft = null;
        if (attempt.Disposition == AiExecutionDisposition.COMPLETED && attempt.CanonicalDraftJson is not null)
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
            draftCommand.Parameters.AddWithValue("tenant", attempt.TenantId);
            draftCommand.Parameters.AddWithValue("rfq", attempt.QuoteRequestId);
            draftCommand.Parameters.AddWithValue("attempt", attemptId);
            draftCommand.Parameters.AddWithValue("canonical", attempt.CanonicalDraftJson);
            await using var reader = await draftCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Canonical RFQ draft upsert returned no row.");
            draft = new(
                attempt.TenantId,
                attempt.QuoteRequestId,
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ReadDateTimeOffset(reader, 3),
                ReadDateTimeOffset(reader, 4));
        }

        // For keyed callers, even REVIEW_MANUAL returns the actual current draft;
        // it may already contain human edits or a newer distinct-key attempt.
        if (identity is not null)
            draft = await FindCurrentDraftCoreAsync(connection, transaction,
                execution.TenantId, execution.QuoteRequestId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var storedExecution = new StoredRfqExtractionExecution(
            auditId,
            execution.TenantId,
            execution.QuoteRequestId,
            execution.ModelId,
            execution.PromptVersion,
            execution.SchemaVersion,
            execution.RequestFingerprint,
            execution.Disposition,
            execution.Code,
            auditCreatedAt);
        var storedAttempt = new StoredRfqExtractionAttempt(
            attemptId,
            attempt.TenantId,
            attempt.QuoteRequestId,
            attempt.ModelId,
            attempt.PromptVersion,
            attempt.SchemaVersion,
            attempt.RequestFingerprint,
            attempt.Disposition,
            attempt.Code,
            attempt.SourceLineageJson,
            attempt.RawProviderOutput,
            attemptCreatedAt);

        return new(storedExecution, new(storedAttempt, draft));
    }

    private static async Task LockKeyedScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId,
        Guid quoteRequestId, RfqExtractionIdempotencyIdentity identity,
        CancellationToken cancellationToken)
    {
        // JSON array makes the scope unambiguous without normalizing key casing,
        // Unicode, or whitespace. Key and hash are not interchangeable.
        var scope = JsonSerializer.Serialize(new[]
        {
            tenantId.ToString("D"), quoteRequestId.ToString("D"), identity.Version, identity.Key
        });
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        var lockId = BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lockId)", connection, transaction);
        command.Parameters.AddWithValue("lockId", lockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RfqExtractionAtomicPersistenceResult?> FindKeyedCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid tenantId, Guid quoteRequestId, RfqExtractionIdempotencyIdentity identity,
        CancellationToken cancellationToken)
    {
        // One immutable attempt owns the audit pointer. Always return DB values
        // instead of the proposed payload of a replaying caller.
        await using var command = new NpgsqlCommand("""
            SELECT a.id,a.model_id,a.prompt_version,a.schema_version,a.request_fingerprint,
                   a.disposition,a.result_code,a.source_lineage::text,a.raw_provider_output,
                   a.created_at,a.idempotency_request_hash,a.audit_event_id,e.created_at
            FROM rfq_extraction_attempts a
            LEFT JOIN audit_events e ON e.id=a.audit_event_id AND e.tenant_id=a.tenant_id
            WHERE a.tenant_id=@tenant AND a.quote_request_id=@rfq
              AND a.idempotency_version=@version AND a.idempotency_key=@key
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        command.Parameters.AddWithValue("version", identity.Version);
        command.Parameters.AddWithValue("key", identity.Key);

        StoredRfqExtractionAttempt attempt;
        StoredRfqExtractionExecution execution;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var storedHash = reader.GetString(10).TrimEnd(' ');
            if (!StringComparer.Ordinal.Equals(storedHash, identity.RequestHash))
                throw new DomainValidationException("RFQ_EXTRACTION_IDEMPOTENCY_CONFLICT",
                    "The extraction key already belongs to a different request hash.");
            if (reader.IsDBNull(11) || reader.IsDBNull(12))
                throw new InvalidOperationException("Keyed extraction is missing its immutable audit pointer.");

            var disposition = Enum.Parse<AiExecutionDisposition>(reader.GetString(5), false);
            var fingerprint = reader.IsDBNull(4) ? null : reader.GetString(4);
            var code = reader.IsDBNull(6) ? null : reader.GetString(6);
            var model = reader.GetString(1);
            var prompt = reader.GetString(2);
            var schema = reader.GetString(3);
            attempt = new(reader.GetGuid(0), tenantId, quoteRequestId, model,
                prompt, schema, fingerprint, disposition, code, reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                ReadDateTimeOffset(reader, 9));
            execution = new(reader.GetGuid(11), tenantId, quoteRequestId, model,
                prompt, schema, fingerprint, disposition, code, ReadDateTimeOffset(reader, 12));
        }

        var draft = await FindCurrentDraftCoreAsync(connection, transaction,
            tenantId, quoteRequestId, cancellationToken);
        return new(execution, new(attempt, draft));
    }

    private static async Task<StoredRfqCanonicalDraft?> FindCurrentDraftCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid tenantId, Guid quoteRequestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT source_attempt_id,canonical_json::text,row_version,created_at,updated_at
            FROM rfq_canonical_drafts
            WHERE tenant_id=@tenant AND quote_request_id=@rfq
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(tenantId, quoteRequestId, reader.GetGuid(0), reader.GetString(1),
            reader.GetInt64(2), ReadDateTimeOffset(reader, 3), ReadDateTimeOffset(reader, 4));
    }

    private static void ValidateKeyedIdentity(RfqExtractionIdempotencyIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Version) || identity.Version.Any(char.IsControl))
            throw new DomainValidationException("RFQ_EXTRACTION_IDEMPOTENCY_IDENTITY_INVALID",
                "Idempotency identity version must be non-empty and contain no control characters.");
        if (string.IsNullOrWhiteSpace(identity.Key)
            || identity.Key.Length > RfqExtractionIdempotencyV1.MaxKeyLength
            || identity.Key.Any(char.IsControl))
            throw new DomainValidationException("RFQ_EXTRACTION_IDEMPOTENCY_KEY_INVALID",
                "Idempotency key is invalid.");
        if (identity.RequestHash is null || identity.RequestHash.Length != 64
            || identity.RequestHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new DomainValidationException("RFQ_EXTRACTION_IDEMPOTENCY_IDENTITY_INVALID",
                "Idempotency request hash must be exactly 64 lowercase hex characters.");
    }

    public async Task<StoredRfqExtractionExecution> SaveAsync(
        RfqExtractionExecutionWrite write,
        CancellationToken cancellationToken = default)
    {
        ValidateAudit(write);
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
            INSERT INTO audit_events(tenant_id,entity_type,entity_id,action,new_value,source)
            SELECT q.tenant_id,'quote_requests',q.id,@action,CAST(@payload AS jsonb),@source
            FROM quote_requests q
            WHERE q.tenant_id=@tenant AND q.id=@rfq
            RETURNING id,created_at
            """);
        command.Parameters.AddWithValue("tenant", write.TenantId);
        command.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        command.Parameters.AddWithValue("action", Action);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("source", Source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_RFQ_NOT_FOUND",
                "The RFQ does not exist in the requested tenant.");
        return new(reader.GetGuid(0), write.TenantId, write.QuoteRequestId, write.ModelId,
            write.PromptVersion, write.SchemaVersion, write.RequestFingerprint, write.Disposition,
            write.Code, reader.GetFieldValue<DateTimeOffset>(1));
    }

    public async Task<RfqExtractionPersistenceResult> SaveAttemptAsync(
        RfqExtractionAttemptWrite write, CancellationToken cancellationToken = default)
    {
        ValidateAttempt(write);
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
        var createdAt = ToDateTimeOffset(createdValue);

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
                reader.GetInt64(2), ReadDateTimeOffset(reader, 3), ReadDateTimeOffset(reader, 4));
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
            results.Add(new(reader.GetGuid(0), tenantId, quoteRequestId, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                Enum.Parse<AiExecutionDisposition>(reader.GetString(5), false),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), ReadDateTimeOffset(reader, 9)));
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
            ReadDateTimeOffset(reader, 3), ReadDateTimeOffset(reader, 4));
    }

    private static void ValidateAtomicConsistency(
        RfqExtractionExecutionWrite execution,
        RfqExtractionAttemptWrite attempt)
    {
        if (execution.TenantId != attempt.TenantId
            || execution.QuoteRequestId != attempt.QuoteRequestId
            || !StringComparer.Ordinal.Equals(execution.ModelId, attempt.ModelId)
            || !StringComparer.Ordinal.Equals(execution.PromptVersion, attempt.PromptVersion)
            || !StringComparer.Ordinal.Equals(execution.SchemaVersion, attempt.SchemaVersion)
            || !StringComparer.Ordinal.Equals(execution.RequestFingerprint, attempt.RequestFingerprint)
            || execution.Disposition != attempt.Disposition
            || !StringComparer.Ordinal.Equals(execution.Code, attempt.Code))
            throw new DomainValidationException(
                "RFQ_EXTRACTION_ATOMIC_WRITE_MISMATCH",
                "Execution audit and extraction attempt must describe the same extraction result.");
    }

    private static void ValidateAudit(RfqExtractionExecutionWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateIdentity(write.TenantId, write.QuoteRequestId);
        ValidateVersions(write.ModelId, write.PromptVersion, write.SchemaVersion, "RFQ_EXTRACTION_AUDIT_VERSION_INVALID");
        ValidateFingerprint(write.RequestFingerprint, "RFQ_EXTRACTION_AUDIT_FINGERPRINT_INVALID");
        if (write.Code is not null && string.IsNullOrWhiteSpace(write.Code))
            throw new DomainValidationException("RFQ_EXTRACTION_AUDIT_CODE_INVALID", "Result code must be null or non-empty.");
    }

    private static void ValidateAttempt(RfqExtractionAttemptWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateIdentity(write.TenantId, write.QuoteRequestId);
        ValidateVersions(write.ModelId, write.PromptVersion, write.SchemaVersion, "RFQ_EXTRACTION_HISTORY_VERSION_INVALID");
        ValidateFingerprint(write.RequestFingerprint, "RFQ_EXTRACTION_HISTORY_FINGERPRINT_INVALID");
        if (write.Code is not null && string.IsNullOrWhiteSpace(write.Code))
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_CODE_INVALID", "Result code must be null or non-empty.");
        ValidateJson(write.SourceLineageJson, JsonValueKind.Array, "RFQ_EXTRACTION_HISTORY_LINEAGE_INVALID");
        if (write.CanonicalDraftJson is not null)
        {
            var canonicalGuard = RfqExtractorOutputGuardV1.Evaluate(write.CanonicalDraftJson);
            if (!canonicalGuard.IsPass)
                throw new DomainValidationException("RFQ_EXTRACTION_DRAFT_INVALID",
                    "Current draft must satisfy the existing CanonicalRFQ v1 contract.");
        }
        if (write.Disposition != AiExecutionDisposition.COMPLETED && write.CanonicalDraftJson is not null)
            throw new DomainValidationException("RFQ_EXTRACTION_DRAFT_DISPOSITION_INVALID",
                "Only a completed validated extraction may initialize the current draft.");
    }

    private static void ValidateVersions(string model, string prompt, string schema, string code)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(schema))
            throw new DomainValidationException(code, "Model, prompt and schema versions are required.");
    }

    private static void ValidateFingerprint(string? fingerprint, string code)
    {
        if (fingerprint is not null && (fingerprint.Length != 64 || fingerprint.Any(c =>
            c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new DomainValidationException(code, "Request fingerprint must be null or lowercase 64-hex.");
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

    private static DateTimeOffset ReadDateTimeOffset(NpgsqlDataReader reader, int ordinal) =>
        ToDateTimeOffset(reader.GetValue(ordinal));

    private static DateTimeOffset ToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException($"Unexpected PostgreSQL timestamp value type: {value.GetType().FullName}.")
    };

    private static void ValidateIdentity(Guid tenantId, Guid quoteRequestId)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty)
            throw new DomainValidationException("RFQ_EXTRACTION_HISTORY_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required.");
    }
}
