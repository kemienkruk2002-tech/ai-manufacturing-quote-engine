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
        await using (var customer = db.DataSource.CreateCommand("""
            INSERT INTO customers(id,tenant_id,name) VALUES(@id,@tenant,'RFQ list customer')
            """))
        {
            customer.Parameters.AddWithValue("id", customerId);
            customer.Parameters.AddWithValue("tenant", PostgresFixture.TenantId);
            await customer.ExecuteNonQueryAsync();
        }
        var match = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.DataReview,
            CustomerId: customerId, RequestedDueDate: new DateOnly(2026, 10, 20));
        var other = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.New,
            RequestedDueDate: new DateOnly(2026, 11, 20));
        await Repository.CreateAsync(match);
        await Repository.CreateAsync(other);

        var rows = await Repository.ListAsync(PostgresFixture.TenantId,
            new(QuoteStatus.DataReview, customerId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Contains(rows, row => row.Id == match.Id);
        Assert.DoesNotContain(rows, row => row.Id == other.Id);
    }

    [Fact]
    public async Task Progressed_status_requires_existing_calculation_inputs()
    {
        var request = new QuoteRequest(PostgresFixture.TenantId, Guid.NewGuid(), null, null, QuoteStatus.ReadyForCalc);
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.CreateAsync(request));
    }
}
