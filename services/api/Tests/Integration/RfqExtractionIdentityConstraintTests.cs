using Npgsql;
using NpgsqlTypes;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqExtractionIdentityConstraintTests(PostgresFixture db)
{
    private const string IdentityVersion = "identity-v1";
    private const string IdentityKey = "retry-42";
    private static readonly string ValidHash = new('a', 64);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Partially_null_identity_is_rejected_even_when_check_would_be_unknown(
        bool hasVersion, bool hasKey, bool hasHash)
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        await RejectAttemptAsync(PostgresFixture.TenantId, rfq,
            hasVersion ? IdentityVersion : null,
            hasKey ? IdentityKey : null,
            hasHash ? ValidHash : null,
            null, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Legacy_all_null_identity_and_null_audit_pointer_remain_valid()
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        var first = await InsertAttemptAsync(PostgresFixture.TenantId, rfq, null, null, null, null);
        var second = await InsertAttemptAsync(PostgresFixture.TenantId, rfq, null, null, null, null);

        Assert.NotEqual(first, second);
        Assert.Equal(2L, await db.ScalarAsync<long>($"""
            SELECT count(*) FROM rfq_extraction_attempts
            WHERE id IN ('{first}', '{second}') AND audit_event_id IS NULL
              AND idempotency_version IS NULL AND idempotency_key IS NULL
              AND idempotency_request_hash IS NULL
            """));
    }

    [Fact]
    public async Task Complete_identity_is_accepted()
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        var id = await InsertAttemptAsync(PostgresFixture.TenantId, rfq, IdentityVersion, IdentityKey, ValidHash, null);

        Assert.Equal(1L, await db.ScalarAsync<long>($"""
            SELECT count(*) FROM rfq_extraction_attempts
            WHERE id='{id}' AND idempotency_version='{IdentityVersion}'
              AND idempotency_key='{IdentityKey}' AND idempotency_request_hash='{ValidHash}'
            """));
    }

    [Theory]
    [InlineData("", "retry", "valid")]
    [InlineData("   ", "retry", "valid")]
    [InlineData("v1", "", "valid")]
    [InlineData("v1", " \t ", "valid")]
    [InlineData("v1", "retry", "uppercase")]
    [InlineData("v1", "retry", "short")]
    [InlineData("v1", "retry", "not-hex")]
    public async Task Blank_or_invalid_complete_identity_is_rejected(
        string version, string key, string hashCase)
    {
        var hash = hashCase switch
        {
            "valid" => ValidHash,
            "uppercase" => new string('A', 64),
            "short" => new string('a', 63),
            _ => new string('g', 64)
        };
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        await RejectAttemptAsync(PostgresFixture.TenantId, rfq, version, key, hash, null,
            PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_tenant_rfq_version_and_key_are_unique_even_if_request_hash_changes(bool changeHash)
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        await InsertAttemptAsync(PostgresFixture.TenantId, rfq, IdentityVersion, IdentityKey, ValidHash, null);

        await RejectAttemptAsync(PostgresFixture.TenantId, rfq, IdentityVersion, IdentityKey,
            changeHash ? new string('b', 64) : ValidHash, null, PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("rfq")]
    [InlineData("version")]
    [InlineData("key")]
    public async Task Different_uniqueness_scopes_are_independent(string differingDimension)
    {
        var tenant = PostgresFixture.TenantId;
        var rfq = await CreateRfqAsync(tenant);
        await InsertAttemptAsync(tenant, rfq, IdentityVersion, IdentityKey, ValidHash, null);

        var secondTenant = differingDimension == "tenant" ? await CreateTenantAsync() : tenant;
        var secondRfq = differingDimension is "tenant" or "rfq"
            ? await CreateRfqAsync(secondTenant)
            : rfq;
        var secondVersion = differingDimension == "version" ? "identity-v2" : IdentityVersion;
        var secondKey = differingDimension == "key" ? "retry-43" : IdentityKey;

        var other = await InsertAttemptAsync(secondTenant, secondRfq, secondVersion, secondKey, ValidHash, null);
        Assert.NotEqual(Guid.Empty, other);
    }

    [Fact]
    public async Task Audit_pointer_to_existing_same_tenant_event_is_valid()
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        var auditId = await CreateAuditAsync(PostgresFixture.TenantId, rfq);
        var attemptId = await InsertAttemptAsync(PostgresFixture.TenantId, rfq,
            IdentityVersion, IdentityKey, ValidHash, auditId);

        Assert.Equal(auditId, await db.ScalarAsync<Guid>($"""
            SELECT audit_event_id FROM rfq_extraction_attempts WHERE id='{attemptId}'
            """));
    }

    [Fact]
    public async Task Audit_pointer_to_unknown_event_is_rejected()
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        await RejectAttemptAsync(PostgresFixture.TenantId, rfq,
            IdentityVersion, IdentityKey, ValidHash, Guid.NewGuid(),
            PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Audit_pointer_to_other_tenant_event_is_rejected()
    {
        var rfq = await CreateRfqAsync(PostgresFixture.TenantId);
        var otherTenant = await CreateTenantAsync();
        var foreignAudit = await CreateAuditAsync(otherTenant, rfq);
        await RejectAttemptAsync(PostgresFixture.TenantId, rfq,
            IdentityVersion, IdentityKey, ValidHash, foreignAudit,
            PostgresErrorCodes.ForeignKeyViolation);
    }

    private async Task RejectAttemptAsync(Guid tenant, Guid rfq, string? version, string? key,
        string? hash, Guid? auditId, string expectedSqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAttemptAsync(tenant, rfq, version, key, hash, auditId));
        Assert.Equal(expectedSqlState, error.SqlState);
    }

    private async Task<Guid> InsertAttemptAsync(Guid tenant, Guid rfq, string? version, string? key,
        string? hash, Guid? auditId)
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO rfq_extraction_attempts
                (id, tenant_id, quote_request_id, model_id, prompt_version, schema_version,
                 disposition, source_lineage, idempotency_version, idempotency_key,
                 idempotency_request_hash, audit_event_id)
            VALUES (@id, @tenant, @rfq, 'model-test', 'prompt-v1', 'schema-v1',
                    'REVIEW_MANUAL', '[]'::jsonb, @version, @key, @hash, @audit)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("rfq", rfq);
        command.Parameters.Add("version", NpgsqlDbType.Text).Value = (object?)version ?? DBNull.Value;
        command.Parameters.Add("key", NpgsqlDbType.Text).Value = (object?)key ?? DBNull.Value;
        command.Parameters.Add("hash", NpgsqlDbType.Text).Value = (object?)hash ?? DBNull.Value;
        command.Parameters.Add("audit", NpgsqlDbType.Uuid).Value = (object?)auditId ?? DBNull.Value;
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> CreateRfqAsync(Guid tenant)
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status,currency)
            VALUES(@id,@tenant,'New','PLN')
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> CreateTenantAsync()
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO tenants(id,name) VALUES(@id,@name)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", "IdentityConstraintTest_" + id.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> CreateAuditAsync(Guid tenant, Guid entityId)
    {
        var id = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO audit_events(id,tenant_id,entity_type,entity_id,action,source)
            VALUES(@id,@tenant,'quote_requests',@entity,'EXTRACTION_TEST','IntegrationTest')
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("entity", entityId);
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
