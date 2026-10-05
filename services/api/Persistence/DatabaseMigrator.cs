using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace QuoteEngine.Persistence;

/// <summary>Transactional, checksum-verified SQL migrations, serialized across processes.</summary>
public sealed class DatabaseMigrator(NpgsqlDataSource dataSource)
{
    private const long MigrationLock = 7044001;
    private const long SeedLock = 7044002;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(@key)", cancellationToken, new NpgsqlParameter("key", MigrationLock));
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version text PRIMARY KEY,
                checksum char(64) NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """, cancellationToken);

        foreach (var resource in Resources("Migrations"))
        {
            var sql = ReadResource(resource);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            await using var check = new NpgsqlCommand("SELECT checksum FROM schema_migrations WHERE version = @version", connection);
            check.Parameters.AddWithValue("version", resource);
            var existing = await check.ExecuteScalarAsync(cancellationToken) as string;
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing, hash))
                    throw new InvalidOperationException($"Applied migration changed: {resource}. Add a new migration instead.");
                continue;
            }
            await ExecuteAsync(connection, sql, cancellationToken);
            await ExecuteAsync(connection,
                "INSERT INTO schema_migrations(version, checksum) VALUES (@version, @checksum)", cancellationToken,
                new("version", resource), new("checksum", hash));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SeedGoldenAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(@key)", cancellationToken, new NpgsqlParameter("key", SeedLock));
        foreach (var resource in Resources("Seed"))
            await ExecuteAsync(connection, ReadResource(resource), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string[] Resources(string folder)
    {
        var resources = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
            .Where(name => name.Contains($".{folder}.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (resources.Length == 0) throw new InvalidOperationException($"No embedded SQL resources in {folder}.");
        return resources;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing SQL resource: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(token);
    }
}
