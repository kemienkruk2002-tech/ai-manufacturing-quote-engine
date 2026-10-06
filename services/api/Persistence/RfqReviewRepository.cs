using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class RfqReviewRepository(NpgsqlDataSource dataSource) : IRfqReviewRepository
{
    public async Task<StoredRfqReview?> AppendAsync(RfqReviewWrite write, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO rfq_canonical_reviews(
                id,tenant_id,quote_request_id,source_attempt_id,draft_row_version,field_path,action,
                corrected_value,actor,source,reason)
            SELECT @id,d.tenant_id,d.quote_request_id,d.source_attempt_id,d.row_version,@path,@action,
                   CASE WHEN @corrected IS NULL THEN NULL ELSE CAST(@corrected AS jsonb) END,@actor,@source,@reason
            FROM rfq_canonical_drafts d
            WHERE d.tenant_id=@tenant AND d.quote_request_id=@rfq AND d.row_version=@version
            RETURNING source_attempt_id,created_at
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", write.TenantId);
        command.Parameters.AddWithValue("rfq", write.QuoteRequestId);
        command.Parameters.AddWithValue("version", write.ExpectedDraftRowVersion);
        command.Parameters.AddWithValue("path", write.FieldPath);
        command.Parameters.AddWithValue("action", write.Action.ToString());
        command.Parameters.AddWithValue("corrected", (object?)write.CorrectedValueJson ?? DBNull.Value);
        command.Parameters.AddWithValue("actor", write.Actor);
        command.Parameters.AddWithValue("source", write.Source);
        command.Parameters.AddWithValue("reason", write.Reason);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(id, write.TenantId, write.QuoteRequestId, reader.GetGuid(0), write.ExpectedDraftRowVersion,
            write.FieldPath, write.Action, write.CorrectedValueJson, write.Actor, write.Source, write.Reason,
            ReadDateTimeOffset(reader, 1));
    }

    public async Task<IReadOnlyList<StoredRfqReview>> ListAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,source_attempt_id,draft_row_version,field_path,action,corrected_value::text,
                   actor,source,reason,created_at
            FROM rfq_canonical_reviews
            WHERE tenant_id=@tenant AND quote_request_id=@rfq
            ORDER BY created_at,id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("rfq", quoteRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredRfqReview>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), tenantId, quoteRequestId, reader.GetGuid(1), reader.GetInt64(2),
                reader.GetString(3), Enum.Parse<RfqReviewAction>(reader.GetString(4), false),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), ReadDateTimeOffset(reader, 9)));
        return result;
    }

    private static DateTimeOffset ReadDateTimeOffset(NpgsqlDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        var value => throw new InvalidOperationException($"Unexpected PostgreSQL timestamp value type: {value.GetType().FullName}.")
    };
}
