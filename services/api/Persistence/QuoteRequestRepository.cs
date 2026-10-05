using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Persistence;

public sealed class QuoteRequestRepository(NpgsqlDataSource dataSource) : IQuoteRequestRepository
{
    public async Task<QuoteRequest> CreateAsync(QuoteRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,customer_id,part_revision_id,external_rfq_no,
                requested_quantity,currency,requested_due_date,status)
            VALUES(@id,@tenant,@customer,@revision,@external,@quantity,@currency,@due,@status)
            RETURNING id,tenant_id,part_revision_id,requested_quantity,status,currency,customer_id,
                external_rfq_no,requested_due_date
            """);
        AddWriteParameters(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("RFQ insert failed.");
        return Read(reader);
    }

    public async Task<QuoteRequest?> FindAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT id,tenant_id,part_revision_id,requested_quantity,status,currency,customer_id,
                external_rfq_no,requested_due_date
            FROM quote_requests WHERE tenant_id=@tenant AND id=@id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("id", quoteRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<QuoteRequest>> ListAsync(Guid tenantId, QuoteRequestFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) return [];
        var conditions = new List<string> { "tenant_id=@tenant" };
        await using var command = dataSource.CreateCommand();
        command.Parameters.AddWithValue("tenant", tenantId);
        if (filter.Status is { } status)
        {
            conditions.Add("status=@status");
            command.Parameters.AddWithValue("status", status.ToString());
        }
        if (filter.CustomerId is { } customerId)
        {
            conditions.Add("customer_id=@customer");
            command.Parameters.AddWithValue("customer", customerId);
        }
        if (filter.DueFrom is { } dueFrom)
        {
            conditions.Add("requested_due_date>=@due_from");
            command.Parameters.AddWithValue("due_from", dueFrom);
        }
        if (filter.DueTo is { } dueTo)
        {
            conditions.Add("requested_due_date<=@due_to");
            command.Parameters.AddWithValue("due_to", dueTo);
        }
        command.CommandText = $"""
            SELECT id,tenant_id,part_revision_id,requested_quantity,status,currency,customer_id,
                external_rfq_no,requested_due_date
            FROM quote_requests WHERE {string.Join(" AND ", conditions)}
            ORDER BY created_at DESC,id
            """;
        var result = new List<QuoteRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    private static void Validate(QuoteRequest request)
    {
        if (request.TenantId == Guid.Empty || request.Id == Guid.Empty)
            throw new ArgumentException("RFQ tenant and id are required.");
        if (request.RequestedQuantity is <= 0) throw new ArgumentOutOfRangeException(nameof(request.RequestedQuantity));
        if (request.Currency.Length != 3 || request.Currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("RFQ currency must be a three-letter uppercase code.");
        if (request.Status is not (QuoteStatus.New or QuoteStatus.DataReview or QuoteStatus.Blocked)
            && (request.PartRevisionId is null || request.RequestedQuantity is null))
            throw new ArgumentException("Progressed RFQ status requires part revision and quantity.");
    }

    private static void AddWriteParameters(NpgsqlCommand command, QuoteRequest request)
    {
        command.Parameters.AddWithValue("id", request.Id);
        command.Parameters.AddWithValue("tenant", request.TenantId);
        command.Parameters.AddWithValue("customer", (object?)request.CustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", (object?)request.PartRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("external", (object?)request.ExternalRfqNo ?? DBNull.Value);
        command.Parameters.AddWithValue("quantity", (object?)request.RequestedQuantity ?? DBNull.Value);
        command.Parameters.AddWithValue("currency", request.Currency);
        command.Parameters.AddWithValue("due", (object?)request.RequestedDueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("status", request.Status.ToString());
    }

    private static QuoteRequest Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(1), reader.GetGuid(0), reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3), Enum.Parse<QuoteStatus>(reader.GetString(4)),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8));
}
