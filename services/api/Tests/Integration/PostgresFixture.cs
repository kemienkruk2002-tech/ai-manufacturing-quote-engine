using Npgsql;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string databaseName = "quoteengine_it_" + Guid.NewGuid().ToString("N");
    private string adminConnectionString = null!;
    public string ConnectionString { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid RevisionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid RouteId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    public static readonly Guid RequestId = Guid.Parse("90000000-0000-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        adminConnectionString = Environment.GetEnvironmentVariable("QUOTEENGINE_TEST_DATABASE")
            ?? throw new InvalidOperationException("Set QUOTEENGINE_TEST_DATABASE to a local PostgreSQL connection with CREATE DATABASE permission. Integration tests never silently skip.");
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin);
        await create.ExecuteNonQueryAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        var migrator = new DatabaseMigrator(DataSource);
        await migrator.MigrateAsync();
        await migrator.SeedGoldenAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (adminConnectionString is null) return;
        // This identifier is constructed exclusively here, never taken from user configuration.
        if (!databaseName.StartsWith("quoteengine_it_", StringComparison.Ordinal) || databaseName.Length != 47)
            throw new InvalidOperationException("Unexpected integration database identifier.");
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task RejectAsync(string sql, string? expectedState = null)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        });
        if (expectedState is not null) Assert.Equal(expectedState, error.SqlState);
        await transaction.RollbackAsync();
    }
}
