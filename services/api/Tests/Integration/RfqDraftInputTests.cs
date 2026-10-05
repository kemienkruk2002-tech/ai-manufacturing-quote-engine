using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqDraftInputTests(PostgresFixture db)
{
    [Fact]
    public async Task Migrations_one_through_eight_apply_from_scratch()
    {
        Assert.Equal(8L, await db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations"));
    }

    [Theory]
    [InlineData("New")]
    [InlineData("DataReview")]
    [InlineData("Blocked")]
    public async Task Draft_status_accepts_null_part_and_quantity(string status)
    {
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
            VALUES(gen_random_uuid(),@tenant,NULL,NULL,@status)
            """, ("tenant", PostgresFixture.TenantId), ("status", status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Ready_for_calc_rejects_each_missing_required_input(bool missingPart) => db.RejectAsync($"""
        INSERT INTO quote_requests(id,tenant_id,part_revision_id,requested_quantity,status)
        VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}',
            {(missingPart ? "NULL" : $"'{PostgresFixture.RevisionId}'")},
            {(missingPart ? "150" : "NULL")},'ReadyForCalc')
        """, PostgresErrorCodes.CheckViolation);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task Nonpositive_quantity_remains_rejected(int quantity) => db.RejectAsync($"""
        INSERT INTO quote_requests(id,tenant_id,requested_quantity,status)
        VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}',{quantity},'New')
        """, PostgresErrorCodes.CheckViolation);

    [Fact]
    public async Task Draft_without_part_or_quantity_accepts_file_metadata()
    {
        var request = Guid.Parse("90000000-0000-0000-0000-000000000061");
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,status) VALUES(@request,@tenant,'New')
            """, ("request", request), ("tenant", PostgresFixture.TenantId));
        var content = "draft-rfq"u8.ToArray();
        var version = await new RfqFileRepository(db.DataSource).GetOrCreateVersionAsync(new(
            PostgresFixture.TenantId, request, "customer-drawing", "drawing.pdf", "application/pdf",
            content.LongLength, RfqFileSha256.Compute(content), "b061-test"));

        Assert.Equal(1, version.VersionNo);
        Assert.Equal(request, version.QuoteRequestId);
    }

    [Fact]
    public async Task Build_blocks_with_part_revision_missing_before_reading_quantity()
    {
        var request = Guid.Parse("90000000-0000-0000-0000-000000000062");
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,requested_quantity,status)
            VALUES(@request,@tenant,150,'DataReview')
            """, ("request", request), ("tenant", PostgresFixture.TenantId));
        var error = await Assert.ThrowsAsync<MissingSnapshotInputException>(() =>
            BuildAsync(request));
        Assert.Equal("PART_REVISION_MISSING", error.Code);
    }

    [Fact]
    public async Task Build_blocks_with_quantity_missing_after_part_is_known()
    {
        var request = Guid.Parse("90000000-0000-0000-0000-000000000063");
        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,part_revision_id,status)
            VALUES(@request,@tenant,@revision,'DataReview')
            """, ("request", request), ("tenant", PostgresFixture.TenantId),
            ("revision", PostgresFixture.RevisionId));
        var error = await Assert.ThrowsAsync<MissingSnapshotInputException>(() =>
            BuildAsync(request));
        Assert.Equal("QUANTITY_MISSING", error.Code);
    }

    [Fact]
    public async Task Complete_W07044_keeps_exact_result_and_deterministic_hashes()
    {
        var service = new CalculationService(new SnapshotInputRepository(db.DataSource),
            new QuoteSnapshotRepository(db.DataSource), new CalculationRunRepository(db.DataSource),
            TimeProvider.System, NullLogger<CalculationService>.Instance);
        var first = await service.CalculateAsync(GoldenCase.Request, "b061-smoke-1");
        var second = await service.CalculateAsync(GoldenCase.Request, "b061-smoke-2");

        Assert.Equal(CalculationStatus.Success, first.Status);
        Assert.Equal(1375m, first.Time.SumTjSecPerUnit);
        Assert.Equal(230m, first.Time.SumTpzMinPerBatch);
        Assert.Equal(1467m, first.Time.LaborSecPerUnit);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(first.CalculationHash, second.CalculationHash);
    }

    private Task<PreparedSnapshot> BuildAsync(Guid requestId) =>
        new SnapshotInputRepository(db.DataSource).BuildAsync(GoldenCase.Request with { QuoteRequestId = requestId });

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
