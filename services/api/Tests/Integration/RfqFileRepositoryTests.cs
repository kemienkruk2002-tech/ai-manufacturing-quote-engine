using System.Text;
using Npgsql;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqFileRepositoryTests(PostgresFixture db)
{
    private RfqFileRepository Repository => new(db.DataSource);

    [Fact]
    public async Task First_save_creates_version_one_without_file_bytes()
    {
        var input = Input("drawing-first", "drawing-v1");
        var stored = await Repository.GetOrCreateVersionAsync(input);

        Assert.Equal(1, stored.VersionNo);
        Assert.Equal(input.Sha256, stored.Sha256);
        Assert.Equal(input.ByteSize, stored.ByteSize);
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_name IN ('quote_request_documents','quote_request_document_versions')
              AND data_type='bytea'
            """));
    }

    [Fact]
    public async Task Same_hash_is_idempotent_and_does_not_add_a_row()
    {
        var input = Input("drawing-idempotent", "same-content");
        var first = await Repository.GetOrCreateVersionAsync(input);
        var second = await Repository.GetOrCreateVersionAsync(input with { OriginalFileName = "renamed.pdf" });
        var versions = await Repository.ListVersionsAsync(input.TenantId, input.QuoteRequestId, input.LogicalKey);

        Assert.Equal(first, second);
        Assert.Single(versions);
    }

    [Fact]
    public async Task Different_hash_creates_version_two_and_preserves_version_one()
    {
        var firstInput = Input("drawing-versioned", "version-one");
        var first = await Repository.GetOrCreateVersionAsync(firstInput);
        var second = await Repository.GetOrCreateVersionAsync(Input("drawing-versioned", "version-two"));
        var versions = await Repository.ListVersionsAsync(firstInput.TenantId, firstInput.QuoteRequestId,
            firstInput.LogicalKey);

        Assert.Equal(1, first.VersionNo);
        Assert.Equal(2, second.VersionNo);
        Assert.Equal([first.Id, second.Id], versions.Select(x => x.Id));
        Assert.Equal(first.Sha256, versions[0].Sha256);
    }

    [Fact]
    public async Task Tenant_scope_prevents_visibility_and_collision()
    {
        var secondTenant = Guid.Parse("00000000-0000-0000-0000-000000000056");
        var secondPart = Guid.Parse("10000000-0000-0000-0000-000000000056");
        var secondRevision = Guid.Parse("20000000-0000-0000-0000-000000000056");
        var secondRequest = Guid.Parse("90000000-0000-0000-0000-000000000056");
        await ExecuteAsync("""
            INSERT INTO tenants(id,name) VALUES(@tenant,'RFQ tenant B');
            INSERT INTO parts(id,tenant_id,part_number,name) VALUES(@part,@tenant,'RFQ-B','RFQ B');
            INSERT INTO part_revisions(id,tenant_id,part_id,status) VALUES(@revision,@tenant,@part,'Draft');
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity)
            VALUES(@request,@tenant,@revision,1);
            """, ("tenant", secondTenant), ("part", secondPart), ("revision", secondRevision),
            ("request", secondRequest));

        var tenantA = await Repository.GetOrCreateVersionAsync(Input("shared-logical-key", "shared-content"));
        var tenantBInput = Input("shared-logical-key", "shared-content") with
        {
            TenantId = secondTenant,
            QuoteRequestId = secondRequest
        };
        var tenantB = await Repository.GetOrCreateVersionAsync(tenantBInput);

        Assert.Equal(1, tenantA.VersionNo);
        Assert.Equal(1, tenantB.VersionNo);
        Assert.NotEqual(tenantA.DocumentId, tenantB.DocumentId);
        Assert.Empty(await Repository.ListVersionsAsync(secondTenant, PostgresFixture.RequestId, "shared-logical-key"));
        Assert.Single(await Repository.ListVersionsAsync(secondTenant, secondRequest, "shared-logical-key"));
    }

    [Theory]
    [InlineData("'bad-hash'", "1", "2")]
    [InlineData("repeat('a',64)", "-1", "2")]
    [InlineData("repeat('b',64)", "1", "0")]
    public async Task Database_rejects_invalid_hash_size_and_version(string hash, string size, string version)
    {
        var existing = await Repository.GetOrCreateVersionAsync(Input("invalid-constraints", "base-content"));
        await db.RejectAsync($"""
            INSERT INTO quote_request_document_versions(id,tenant_id,document_id,version_no,
                original_file_name,byte_size,sha256)
            VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}','{existing.DocumentId}',{version},
                'invalid.bin',{size},{hash})
            """, PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("UPDATE quote_request_document_versions SET original_file_name='changed' WHERE id='{0}'")]
    [InlineData("DELETE FROM quote_request_document_versions WHERE id='{0}'")]
    [InlineData("TRUNCATE quote_request_document_versions")]
    public async Task Database_rejects_history_mutation(string sql)
    {
        var existing = await Repository.GetOrCreateVersionAsync(Input("immutable-history", "immutable"));
        await db.RejectAsync(string.Format(sql, existing.Id), PostgresErrorCodes.CheckViolation);
    }

    private static RfqFileVersionInput Input(string logicalKey, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new(PostgresFixture.TenantId, PostgresFixture.RequestId, logicalKey, "drawing.pdf",
            "application/pdf", bytes.LongLength, RfqFileSha256.Compute(bytes), "rfq-test");
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
