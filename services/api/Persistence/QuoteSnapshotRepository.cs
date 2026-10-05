using System.Security.Cryptography;
using System.Text;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Persistence;

public sealed class QuoteSnapshotRepository(NpgsqlDataSource dataSource) : IQuoteSnapshotRepository
{
    public async Task<StoredSnapshot> GetOrCreateAsync(Guid quoteRequestId, PreparedSnapshot prepared, CancellationToken cancellationToken = default)
    {
        var payload = prepared.Payload;
        var json = CanonicalSnapshotDispatcher.Serialize(payload);
        var hash = CanonicalSnapshotDispatcher.ComputeSnapshotHash(payload);
        var id = StableId.FromHash(hash);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = new NpgsqlCommand("""
            SELECT part_revision_id,requested_quantity FROM quote_requests WHERE tenant_id=@tenant AND id=@request FOR UPDATE
            """, connection))
        {
            check.Parameters.AddWithValue("tenant", payload.TenantId);
            check.Parameters.AddWithValue("request", quoteRequestId);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new MissingSnapshotInputException("QUOTE_REQUEST_MISSING", "Quote request is missing for this tenant.");
            if (reader.GetGuid(0) != prepared.SourceRevisionId || reader.GetInt32(1) != payload.Quantity)
                throw new MissingSnapshotInputException("STALE_CONFIGURATION", "Quote request changed while its snapshot was being prepared. Build a new snapshot.");
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO quote_snapshots(id,tenant_id,quote_request_id,source_route_id,canonical_json,snapshot_hash)
            VALUES(@id,@tenant,@request,@source_route,@json,@hash)
            ON CONFLICT (tenant_id,snapshot_hash) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", payload.TenantId);
        command.Parameters.AddWithValue("request", quoteRequestId);
        command.Parameters.AddWithValue("source_route", prepared.SourceRouteId);
        command.Parameters.AddWithValue("json", json);
        command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
        var stored = await FindAsync(connection, payload.TenantId, hash, cancellationToken)
            ?? throw new InvalidOperationException("Snapshot insert/read failed.");
        if (!StringComparer.Ordinal.Equals(stored.CanonicalJson, json))
            throw new InvalidOperationException("Snapshot hash collision or corrupted canonical payload.");
        // A content-addressed snapshot can be shared by multiple requests; preserve every association.
        // quote_snapshots.quote_request_id retains only original creation provenance.
        await using var link = new NpgsqlCommand("""
            INSERT INTO quote_request_snapshots(tenant_id,quote_request_id,quote_snapshot_id)
            VALUES(@tenant,@request,@snapshot) ON CONFLICT DO NOTHING
            """, connection);
        link.Parameters.AddWithValue("tenant", payload.TenantId);
        link.Parameters.AddWithValue("request", quoteRequestId);
        link.Parameters.AddWithValue("snapshot", stored.Id);
        await link.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    public async Task<StoredSnapshot?> FindAsync(Guid tenantId, string snapshotHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, tenantId, snapshotHash, cancellationToken);
    }

    private static async Task<StoredSnapshot?> FindAsync(NpgsqlConnection connection, Guid tenantId, string snapshotHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id,quote_request_id,source_route_id,canonical_json,snapshot_hash FROM quote_snapshots
            WHERE tenant_id=@tenant AND snapshot_hash=@hash
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("hash", snapshotHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredSnapshot(reader.GetGuid(0), tenantId, reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4)) : null;
    }
}

internal static class StableId
{
    public static Guid FromHash(string hash) => Guid.ParseExact(hash[..32], "N");
    public static Guid FromText(string value) => FromHash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant());
}
