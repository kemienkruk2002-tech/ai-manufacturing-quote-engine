using QuoteEngine.Application;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class QuoteRequestRepositoryTests(PostgresFixture db)
{
    private QuoteRequestRepository Repository => new(db.DataSource);

    [Fact]
    public async Task Draft_round_trips_without_part_or_quantity()
    {
        var request = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null,
            QuoteStatus.New, ExternalRfqNo: $"RFQ-{Guid.NewGuid():N}");
        Assert.Equal(request, await Repository.CreateAsync(request));
        Assert.Equal(request, await Repository.FindAsync(request.TenantId, request.Id));
    }

    [Fact]
    public async Task Tenant_filter_hides_request()
    {
        var request = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.New);
        await Repository.CreateAsync(request);
        Assert.Null(await Repository.FindAsync(Guid.NewGuid(), request.Id));
        Assert.Empty(await Repository.ListAsync(Guid.NewGuid(), new()));
    }

    [Fact]
    public async Task List_filters_by_existing_status_customer_and_due_date_fields()
    {
        var customerId = Guid.NewGuid();
        await using (var customer = db.DataSource.CreateCommand(
                         "INSERT INTO customers(id,tenant_id,name) VALUES(@id,@tenant,'RFQ list customer')"))
        {
            customer.Parameters.AddWithValue("id", customerId);
            customer.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await customer.ExecuteNonQueryAsync();
        }

        var matchId = Guid.NewGuid();
        await using (var match = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,customer_id,status,requested_due_date)
            VALUES(@id,@tenant,@customer,'DataReview',@due)
            """))
        {
            match.Parameters.AddWithValue("id", matchId);
            match.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            match.Parameters.AddWithValue("customer", customerId);
            match.Parameters.AddWithValue("due", new DateOnly(2026, 10, 20));
            await match.ExecuteNonQueryAsync();
        }

        var other = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.New,
            RequestedDueDate: new DateOnly(2026, 11, 20));
        await Repository.CreateAsync(other);

        var rows = await Repository.ListAsync(PostgresFixture.TenantId,
            new(QuoteStatus.DataReview, customerId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Contains(rows, row => row.Id == matchId);
        Assert.DoesNotContain(rows, row => row.Id == other.Id);
    }

    [Theory]
    [InlineData(QuoteStatus.DataReview)]
    [InlineData(QuoteStatus.ReadyForCalc)]
    [InlineData(QuoteStatus.Calculated)]
    [InlineData(QuoteStatus.Approved)]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Lost)]
    [InlineData(QuoteStatus.Won)]
    [InlineData(QuoteStatus.Blocked)]
    public async Task Create_rejects_every_non_draft_status(QuoteStatus status)
    {
        var request = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), PostgresFixture.RevisionId, 1, status);

        await Assert.ThrowsAsync<ArgumentException>(() => Repository.CreateAsync(request));
        Assert.Null(await Repository.FindAsync(request.TenantId, request.Id));
    }

    [Fact]
    public async Task Simultaneous_draft_updates_allow_exactly_one_writer_for_same_version()
    {
        var created = await Repository.CreateAsync(new QuoteRequest(
            PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.New));
        var first = new QuoteDraftUpdate(null, null, 10, "PLN", "first", null, created.RowVersion);
        var second = new QuoteDraftUpdate(null, null, 20, "PLN", "second", null, created.RowVersion);
        var results = await Task.WhenAll(
            Repository.UpdateDraftAsync(created.TenantId, created.Id, first),
            Repository.UpdateDraftAsync(created.TenantId, created.Id, second));

        Assert.Single(results, result => result is not null);
        Assert.Single(results, result => result is null);
        var stored = await Repository.FindAsync(created.TenantId, created.Id);
        Assert.NotNull(stored);
        Assert.Equal(created.RowVersion + 1, stored.RowVersion);
        Assert.Contains(stored.RequestedQuantity, new int?[] { 10, 20 });
    }

    [Fact]
    public async Task Draft_update_rejects_stale_version_and_non_draft_status()
    {
        var draft = await Repository.CreateAsync(new QuoteRequest(
            PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.New));
        var updated = await Repository.UpdateDraftAsync(draft.TenantId, draft.Id,
            new(null, null, null, "PLN", "v2", null, draft.RowVersion));
        Assert.NotNull(updated);
        Assert.Null(await Repository.UpdateDraftAsync(draft.TenantId, draft.Id,
            new(null, null, null, "PLN", "stale", null, draft.RowVersion)));

        var reviewId = Guid.NewGuid();
        await using (var command = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status) VALUES(@id,@tenant,'DataReview')
            """))
        {
            command.Parameters.AddWithValue("id", reviewId);
            command.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await command.ExecuteNonQueryAsync();
        }

        var review = await Repository.FindAsync(PostgresFixture.TenantId, reviewId);
        Assert.NotNull(review);
        Assert.Null(await Repository.UpdateDraftAsync(review.TenantId, review.Id,
            new(null, null, null, "PLN", "blocked", null, review.RowVersion)));
    }
}
