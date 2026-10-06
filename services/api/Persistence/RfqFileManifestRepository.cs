using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Persistence;

public sealed class RfqFileManifestRepository(NpgsqlDataSource dataSource) : IRfqFileManifestRepository
{
    public async Task<IReadOnlyList<RfqFileManifestDocument>> ListAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty) return [];

        await using var command = dataSource.CreateCommand("""
            SELECT d.id,d.logical_key,v.id,v.version_no,v.original_file_name,v.mime_type,
                v.byte_size,v.sha256,v.created_at,v.source_reference
            FROM quote_request_documents d
            JOIN quote_request_document_versions v
              ON v.tenant_id=d.tenant_id AND v.document_id=d.id
            WHERE d.tenant_id=@tenant AND d.quote_request_id=@request
            ORDER BY d.logical_key,v.version_no
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("request", quoteRequestId);

        var documents = new List<RfqFileManifestDocument>();
        Guid currentDocumentId = Guid.Empty;
        string? currentLogicalKey = null;
        List<RfqFileVersion>? versions = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var documentId = reader.GetGuid(0);
            var logicalKey = reader.GetString(1);
            if (versions is null || documentId != currentDocumentId)
            {
                if (versions is not null)
                    documents.Add(new(currentDocumentId, currentLogicalKey!, versions.AsReadOnly()));
                currentDocumentId = documentId;
                currentLogicalKey = logicalKey;
                versions = [];
            }

            versions.Add(new RfqFileVersion(
                tenantId, reader.GetGuid(2), quoteRequestId, documentId, logicalKey, reader.GetInt32(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6),
                reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        if (versions is not null)
            documents.Add(new(currentDocumentId, currentLogicalKey!, versions.AsReadOnly()));
        return documents.AsReadOnly();
    }
}
