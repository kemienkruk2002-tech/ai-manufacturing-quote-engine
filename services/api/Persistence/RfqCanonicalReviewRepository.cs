using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Persistence;

public sealed class RfqCanonicalReviewRepository(NpgsqlDataSource dataSource)
    : IRfqCanonicalReviewRepository
{
    public const string AuditEntityType = "rfq_canonical_drafts";
    public const string AuditSource = "RfqCanonicalReviewServiceV1";
    public const string ConfirmAction = "RFQ_CANONICAL_FIELD_CONFIRMED";
    public const string RejectAction = "RFQ_CANONICAL_FIELD_REJECTED";
    public const string CorrectAction = "RFQ_CANONICAL_FIELD_CORRECTED";

    public async Task<RfqCanonicalReviewApplyResult> ApplyAsync(
        RfqCanonicalReviewWrite write,
        CancellationToken cancellationToken = default)
    {
        Validate(write);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var update = new NpgsqlCommand("""
            UPDATE rfq_canonical_drafts
            SET canonical_json=CAST(@canonical AS jsonb),
                row_version=row_version+1,
                updated_at=now()
            WHERE tenant_id=@tenant
              AND quote_request_id=@rfq
              AND row_version=@expected
            RETURNING source_attempt_id,canonical_json::text,row_version,created_at,updated_at
            """, connection, transaction);
        update.Parameters.AddWithValue("canonical", write.CanonicalJson);
        update.Parameters.AddWithValue("tenant", write.TenantId);
        update.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        update.Parameters.AddWithValue("expected", write.ExpectedRowVersion);

        StoredRfqCanonicalDraft? draft = null;
        await using (var reader = await update.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                draft = new(
                    write.TenantId,
                    write.QuoteRequestId,
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    ReadDateTimeOffset(reader, 3),
                    ReadDateTimeOffset(reader, 4));
        }

        if (draft is null)
        {
            await using var exists = new NpgsqlCommand("""
                SELECT row_version
                FROM rfq_canonical_drafts
                WHERE tenant_id=@tenant AND quote_request_id=@rfq
                """, connection, transaction);
            exists.Parameters.AddWithValue("tenant", write.TenantId);
            exists.Parameters.AddWithValue("rfq", write.QuoteRequestId);
            var currentVersion = await exists.ExecuteScalarAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return currentVersion is null
                ? new(RfqCanonicalReviewApplyStatus.DRAFT_NOT_FOUND, null, null)
                : new(RfqCanonicalReviewApplyStatus.VERSION_CONFLICT, null, null);
        }

        var action = write.Decision switch
        {
            RfqCanonicalReviewDecision.CONFIRM => ConfirmAction,
            RfqCanonicalReviewDecision.REJECT => RejectAction,
            RfqCanonicalReviewDecision.CORRECT => CorrectAction,
            _ => throw new InvalidOperationException("Unsupported canonical RFQ review decision.")
        };

        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(
                tenant_id,entity_type,entity_id,action,old_value,new_value,user_id,source,correlation_id)
            VALUES (
                @tenant,@entityType,@rfq,@action,
                jsonb_build_object(
                    'field_path',@fieldPath,
                    'fact',CAST(@beforeFact AS jsonb),
                    'row_version',@beforeVersion),
                jsonb_build_object(
                    'field_path',@fieldPath,
                    'decision',@decision,
                    'actor',@actor,
                    'review_source',@reviewSource,
                    'reason',@reason,
                    'source_attempt_id',@sourceAttempt,
                    'fact',CAST(@afterFact AS jsonb),
                    'row_version',@afterVersion),
                NULL,@auditSource,@correlation)
            RETURNING id,created_at
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", write.TenantId);
        audit.Parameters.AddWithValue("entityType", AuditEntityType);
        audit.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("fieldPath", write.FieldPath);
        audit.Parameters.AddWithValue("beforeFact", write.BeforeFactJson);
        audit.Parameters.AddWithValue("beforeVersion", write.ExpectedRowVersion);
        audit.Parameters.AddWithValue("decision", write.Decision.ToString());
        audit.Parameters.AddWithValue("actor", write.Actor);
        audit.Parameters.AddWithValue("reviewSource", write.ReviewSource);
        audit.Parameters.AddWithValue("reason", write.Reason);
        audit.Parameters.AddWithValue("sourceAttempt", draft.SourceAttemptId.ToString("D"));
        audit.Parameters.AddWithValue("afterFact", write.AfterFactJson);
        audit.Parameters.AddWithValue("afterVersion", draft.RowVersion);
        audit.Parameters.AddWithValue("auditSource", AuditSource);
        audit.Parameters.AddWithValue("correlation", (object?)write.CorrelationId ?? DBNull.Value);

        Guid auditId;
        DateTimeOffset createdAt;
        await using (var reader = await audit.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Canonical RFQ review audit insert returned no row.");
            auditId = reader.GetGuid(0);
            createdAt = ReadDateTimeOffset(reader, 1);
        }

        await transaction.CommitAsync(cancellationToken);

        var reviewEvent = new StoredRfqCanonicalReviewEvent(
            auditId,
            write.TenantId,
            write.QuoteRequestId,
            draft.SourceAttemptId,
            write.FieldPath,
            write.Decision,
            write.Actor,
            write.ReviewSource,
            write.Reason,
            write.BeforeFactJson,
            write.AfterFactJson,
            write.ExpectedRowVersion,
            draft.RowVersion,
            write.CorrelationId,
            createdAt);

        return new(RfqCanonicalReviewApplyStatus.UPDATED, draft, reviewEvent);
    }

    public async Task<IReadOnlyList<StoredRfqCanonicalReviewEvent>> ListAsync(
        Guid tenantId,
        Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, quoteRequestId);

        await using var command = dataSource.CreateCommand("""
            SELECT
                id,
                (new_value->>'source_attempt_id')::uuid,
                new_value->>'field_path',
                new_value->>'decision',
                new_value->>'actor',
                new_value->>'review_source',
                new_value->>'reason',
                (old_value->'fact')::text,
                (new_value->'fact')::text,
                (old_value->>'row_version')::bigint,
                (new_value->>'row_version')::bigint,
                correlation_id,
                created_at
            FROM audit_events
            WHERE tenant_id=@tenant
              AND entity_type=@entityType
              AND entity_id=@rfq
              AND source=@auditSource
              AND action IN (@confirm,@reject,@correct)
            ORDER BY created_at,id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("entityType", AuditEntityType);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        command.Parameters.AddWithValue("auditSource", AuditSource);
        command.Parameters.AddWithValue("confirm", ConfirmAction);
        command.Parameters.AddWithValue("reject", RejectAction);
        command.Parameters.AddWithValue("correct", CorrectAction);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<StoredRfqCanonicalReviewEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new(
                reader.GetGuid(0),
                tenantId,
                quoteRequestId,
                reader.GetGuid(1),
                reader.GetString(2),
                Enum.Parse<RfqCanonicalReviewDecision>(reader.GetString(3), false),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                ReadDateTimeOffset(reader, 12)));
        }
        return events;
    }

    private static void Validate(RfqCanonicalReviewWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateIdentity(write.TenantId, write.QuoteRequestId);

        if (write.ExpectedRowVersion < 1)
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_VERSION_INVALID",
                "Expected row version must be positive.");
        if (string.IsNullOrWhiteSpace(write.FieldPath))
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_FIELD_PATH_INVALID",
                "Field path is required.");
        if (string.IsNullOrWhiteSpace(write.Actor))
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_ACTOR_REQUIRED",
                "Authenticated review actor is required.");
        if (string.IsNullOrWhiteSpace(write.ReviewSource))
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_SOURCE_REQUIRED",
                "Review source is required.");
        if (string.IsNullOrWhiteSpace(write.Reason))
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_REASON_REQUIRED",
                "Review reason is required.");

        var guard = RfqExtractorOutputGuardV1.Evaluate(write.CanonicalJson);
        if (!guard.IsPass)
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_DRAFT_INVALID",
                "Reviewed draft must satisfy the existing CanonicalRFQ v1 contract.");

        ValidateFactJson(write.BeforeFactJson, "RFQ_CANONICAL_REVIEW_BEFORE_FACT_INVALID");
        ValidateFactJson(write.AfterFactJson, "RFQ_CANONICAL_REVIEW_AFTER_FACT_INVALID");
    }

    private static void ValidateFactJson(string json, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new DomainValidationException(code, "Review fact must be a JSON object.");
        }
    }

    private static void ValidateIdentity(Guid tenantId, Guid quoteRequestId)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty)
            throw new DomainValidationException("RFQ_CANONICAL_REVIEW_IDENTITY_INVALID",
                "Tenant and RFQ identifiers are required.");
    }

    private static DateTimeOffset ReadDateTimeOffset(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unexpected PostgreSQL timestamp value type: {value.GetType().FullName}.")
        };
    }
}
