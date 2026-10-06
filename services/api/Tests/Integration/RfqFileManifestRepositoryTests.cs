using System.Text;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqFileManifestRepositoryTests(PostgresFixture db)
{
    [Fact]
    public async Task Manifest_groups_logical_documents_preserves_versions_and_source_references()
    {
        var files = new RfqFileRepository(db.DataSource);
        var manifest = new RfqFileManifestRepository(db.DataSource);

        await files.GetOrCreateVersionAsync(Input("manifest-drawing", "drawing-v1", "mail:1"));
        await files.GetOrCreateVersionAsync(Input("manifest-drawing", "drawing-v2", "mail:2"));
        await files.GetOrCreateVersionAsync(Input("manifest-step", "step-v1", "attachment:3"));

        var documents = await manifest.ListAsync(PostgresFixture.TenantId, PostgresFixture.RequestId);
        var drawing = Assert.Single(documents, x => x.LogicalKey == "manifest-drawing");
        var step = Assert.Single(documents, x => x.LogicalKey == "manifest-step");

        Assert.Equal([1, 2], drawing.Versions.Select(x => x.VersionNo));
        Assert.Equal(["mail:1", "mail:2"], drawing.Versions.Select(x => x.SourceReference));
        Assert.Equal("attachment:3", Assert.Single(step.Versions).SourceReference);
        Assert.All(drawing.Versions, version => Assert.Equal(drawing.DocumentId, version.DocumentId));
    }

    [Fact]
    public async Task Manifest_is_tenant_and_rfq_scoped()
    {
        var manifest = new RfqFileManifestRepository(db.DataSource);
        Assert.Empty(await manifest.ListAsync(Guid.NewGuid(), PostgresFixture.RequestId));
        Assert.Empty(await manifest.ListAsync(PostgresFixture.TenantId, Guid.NewGuid()));
    }

    private static RfqFileVersionInput Input(string logicalKey, string content, string sourceReference)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new(PostgresFixture.TenantId, PostgresFixture.RequestId, logicalKey, "source.bin",
            "application/octet-stream", bytes.LongLength, RfqFileSha256.Compute(bytes), sourceReference);
    }
}
