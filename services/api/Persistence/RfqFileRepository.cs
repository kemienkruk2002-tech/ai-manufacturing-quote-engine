using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Persistence;

public sealed class RfqFileRepository(NpgsqlDataSource dataSource) : IRfqFileRepository
{
    public async Task<bool> RequestExistsAsync(Guid tenantId, Guid quoteRequestId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty) return false;
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS(
                SELECT 1 FROM quote_requests WHERE tenant_id=@tenant AND id=@request
            )
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("request", quoteRequestId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<RfqFileVersion> GetOrCreateVersionAsync(RfqFileVersionInput input,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var documentId = StableId.FromText($"rfq-document|{input.TenantId:N}|{input.QuoteRequestId:N}|{input.LogicalKey}");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertDocument = new NpgsqlCommand("""
            INSERT INTO quote_request_documents(id,tenant_id,quote_request_id,logical_key)
            VALUES(@id,@tenant,@request,@logical_key)
            ON CONFLICT (tenant_id,quote_request_id,logical_key) DO NOTHING
            """, connection))
        {
            insertDocument.Parameters.AddWithValue("id", documentId);
            insertDocument.Parameters.AddWithValue("tenant", input.TenantId);
            insertDocument.Parameters.AddWithValue("request", input.QuoteRequestId);
            insertDocument.Parameters.AddWithValue("logical_key", input.LogicalKey);
            await insertDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lockDocument = new NpgsqlCommand("""
            SELECT id FROM quote_request_documents
            WHERE tenant_id=@tenant AND quote_request_id=@request AND logical_key=@logical_key
            FOR UPDATE
            """, connection))
        {
            lockDocument.Parameters.AddWithValue("tenant", input.TenantId);
            lockDocument.Parameters.AddWithValue("request", input.QuoteRequestId);
            lockDocument.Parameters.AddWithValue("logical_key", input.LogicalKey);
            documentId = await lockDocument.ExecuteScalarAsync(cancellationToken) is Guid id
                ? id : throw new InvalidOperationException("RFQ document insert/read failed.");
        }

        var existing = await FindByHashAsync(connection, input.TenantId, documentId, input.Sha256,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        int versionNo;
        await using (var nextVersion = new NpgsqlCommand("""
            SELECT COALESCE(max(version_no),0)+1 FROM quote_request_document_versions
            WHERE tenant_id=@tenant AND document_id=@document
            """, connection))
        {
            nextVersion.Parameters.AddWithValue("tenant", input.TenantId);
            nextVersion.Parameters.AddWithValue("document", documentId);
            versionNo = (int)(await nextVersion.ExecuteScalarAsync(cancellationToken))!;
        }

        var versionId = StableId.FromText($"rfq-document-version|{documentId:N}|{input.Sha256}");
        await using (var insertVersion = new NpgsqlCommand("""
            INSERT INTO quote_request_document_versions(id,tenant_id,document_id,version_no,
                original_file_name,mime_type,byte_size,sha256,source_reference)
            VALUES(@id,@tenant,@document,@version,@file_name,@mime_type,@byte_size,@sha256,@source)
            """, connection))
        {
            insertVersion.Parameters.AddWithValue("id", versionId);
            insertVersion.Parameters.AddWithValue("tenant", input.TenantId);
            insertVersion.Parameters.AddWithValue("document", documentId);
            insertVersion.Parameters.AddWithValue("version", versionNo);
            insertVersion.Parameters.AddWithValue("file_name", input.OriginalFileName);
            insertVersion.Parameters.AddWithValue("mime_type", (object?)input.MimeType ?? DBNull.Value);
            insertVersion.Parameters.AddWithValue("byte_size", input.ByteSize);
            insertVersion.Parameters.AddWithValue("sha256", input.Sha256);
            insertVersion.Parameters.AddWithValue("source", (object?)input.SourceReference ?? DBNull.Value);
            await insertVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        var created = await FindByHashAsync(connection, input.TenantId, documentId, input.Sha256,
            cancellationToken) ?? throw new InvalidOperationException("RFQ file version insert/read failed.");
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<RfqFileVersion>> ListVersionsAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty || string.IsNullOrWhiteSpace(logicalKey))
            throw new DomainValidationException("RFQ_FILE_IDENTITY_INVALID", "Tenant, request and logical key are required.");
        await using var command = dataSource.CreateCommand("""
            SELECT v.id,v.tenant_id,d.quote_request_id,d.id,d.logical_key,v.version_no,
                v.original_file_name,v.mime_type,v.byte_size,v.sha256,v.created_at,v.source_reference
            FROM quote_request_documents d
            JOIN quote_request_document_versions v ON v.tenant_id=d.tenant_id AND v.document_id=d.id
            WHERE d.tenant_id=@tenant AND d.quote_request_id=@request AND d.logical_key=@logical_key
            ORDER BY v.version_no
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("request", quoteRequestId);
        command.Parameters.AddWithValue("logical_key", logicalKey);
        var versions = new List<RfqFileVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) versions.Add(Read(reader));
        return versions.AsReadOnly();
    }

    public async Task<RfqFileVersion?> FindVersionAsync(Guid tenantId, Guid quoteRequestId,
        string logicalKey, int versionNo, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || quoteRequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(logicalKey) || versionNo <= 0) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT v.id,v.tenant_id,d.quote_request_id,d.id,d.logical_key,v.version_no,
                v.original_file_name,v.mime_type,v.byte_size,v.sha256,v.created_at,v.source_reference
            FROM quote_request_documents d
            JOIN quote_request_document_versions v ON v.tenant_id=d.tenant_id AND v.document_id=d.id
            WHERE d.tenant_id=@tenant AND d.quote_request_id=@request
                AND d.logical_key=@logical_key AND v.version_no=@version
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("request", quoteRequestId);
        command.Parameters.AddWithValue("logical_key", logicalKey);
        command.Parameters.AddWithValue("version", versionNo);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<RfqFileVersion?> FindByHashAsync(NpgsqlConnection connection, Guid tenantId,
        Guid documentId, string hash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v.id,v.tenant_id,d.quote_request_id,d.id,d.logical_key,v.version_no,
                v.original_file_name,v.mime_type,v.byte_size,v.sha256,v.created_at,v.source_reference
            FROM quote_request_documents d
            JOIN quote_request_document_versions v ON v.tenant_id=d.tenant_id AND v.document_id=d.id
            WHERE v.tenant_id=@tenant AND v.document_id=@document AND v.sha256=@sha256
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("sha256", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static RfqFileVersion Read(NpgsqlDataReader reader) => new(reader.GetGuid(1), reader.GetGuid(0),
        reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10), reader.IsDBNull(11) ? null : reader.GetString(11));

    private static void Validate(RfqFileVersionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TenantId == Guid.Empty || input.QuoteRequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.LogicalKey))
            throw new DomainValidationException("RFQ_FILE_IDENTITY_INVALID", "Tenant, request and logical key are required.");
        if (string.IsNullOrWhiteSpace(input.OriginalFileName))
            throw new DomainValidationException("RFQ_FILE_NAME_INVALID", "Original file name is required.");
        if (input.MimeType is not null && string.IsNullOrWhiteSpace(input.MimeType))
            throw new DomainValidationException("RFQ_FILE_MIME_INVALID", "MIME type must be null or non-empty.");
        if (input.ByteSize < 0)
            throw new DomainValidationException("RFQ_FILE_SIZE_INVALID", "Byte size cannot be negative.");
        if (input.Sha256.Length != 64 || input.Sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new DomainValidationException("RFQ_FILE_HASH_INVALID", "SHA-256 must be lowercase 64-hex.");
    }
}
