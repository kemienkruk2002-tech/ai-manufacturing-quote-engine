using Npgsql;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Customers;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CustomerRepositoryTests(PostgresFixture db)
{
    private CustomerRepository Repository => new(db.DataSource);

    [Fact]
    public async Task Customer_and_contacts_round_trip_with_deterministic_contact_order()
    {
        var customer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "Customer A");
        var contactHigh = new CustomerContact(PostgresFixture.TenantId,
            Guid.Parse("f0000000-0000-0000-0000-000000000001"), customer.Id, "Contact B");
        var contactLow = new CustomerContact(PostgresFixture.TenantId,
            Guid.Parse("10000000-0000-0000-0000-000000000001"), customer.Id, "Contact A");

        Assert.Equal(customer, await Repository.CreateAsync(customer));
        Assert.Equal(contactHigh, await Repository.CreateContactAsync(contactHigh));
        Assert.Equal(contactLow, await Repository.CreateContactAsync(contactLow));

        Assert.Equal(customer, await Repository.FindAsync(customer.TenantId, customer.Id));
        Assert.Equal(contactHigh, await Repository.FindContactAsync(contactHigh.TenantId, contactHigh.Id));

        var contacts = await Repository.ListContactsAsync(customer.TenantId, customer.Id);
        Assert.Equal([contactLow.Id, contactHigh.Id], contacts.Select(contact => contact.Id));
    }

    [Fact]
    public async Task Tenant_filter_hides_customer_and_contact()
    {
        var customer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "Tenant scoped customer");
        var contact = new CustomerContact(PostgresFixture.TenantId, Guid.NewGuid(), customer.Id, "Tenant scoped contact");
        await Repository.CreateAsync(customer);
        await Repository.CreateContactAsync(contact);

        var otherTenant = Guid.NewGuid();
        await ExecuteAsync("INSERT INTO tenants(id,name) VALUES(@tenant,'Other customer tenant')",
            ("tenant", otherTenant));

        Assert.Null(await Repository.FindAsync(otherTenant, customer.Id));
        Assert.Null(await Repository.FindContactAsync(otherTenant, contact.Id));
        Assert.Empty(await Repository.ListContactsAsync(otherTenant, customer.Id));
    }

    [Fact]
    public async Task Database_rejects_cross_tenant_contact_reference()
    {
        var customer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "Cross tenant customer");
        await Repository.CreateAsync(customer);
        var otherTenant = Guid.NewGuid();
        await ExecuteAsync("INSERT INTO tenants(id,name) VALUES(@tenant,'Other contact tenant')",
            ("tenant", otherTenant));

        await db.RejectAsync($"""
            INSERT INTO customer_contacts(id,tenant_id,customer_id,name)
            VALUES(gen_random_uuid(),'{otherTenant}','{customer.Id}','Invalid cross tenant contact')
            """, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Quote_request_can_reference_same_tenant_customer()
    {
        var customer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "RFQ customer");
        await Repository.CreateAsync(customer);
        var requestId = Guid.NewGuid();

        await ExecuteAsync("""
            INSERT INTO quote_requests(id,tenant_id,customer_id,status)
            VALUES(@request,@tenant,@customer,'New')
            """, ("request", requestId), ("tenant", customer.TenantId), ("customer", customer.Id));

        Assert.Equal(customer.Id, await ScalarAsync<Guid?>(
            "SELECT customer_id FROM quote_requests WHERE tenant_id=@tenant AND id=@request",
            ("tenant", customer.TenantId), ("request", requestId)));
    }

    [Fact]
    public async Task Database_rejects_cross_tenant_quote_customer_reference()
    {
        var otherTenant = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO tenants(id,name) VALUES(@tenant,'Other RFQ customer tenant');
            INSERT INTO customers(id,tenant_id,name) VALUES(@customer,@tenant,'Other tenant customer');
            """, ("tenant", otherTenant), ("customer", otherCustomer));

        await db.RejectAsync($"""
            INSERT INTO quote_requests(id,tenant_id,customer_id,status)
            VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}','{otherCustomer}','New')
            """, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("contact")]
    public async Task Whitespace_names_are_rejected_in_domain_and_database(string entity)
    {
        if (entity == "customer")
        {
            var customer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "   ");
            Assert.Throws<DomainValidationException>(customer.Validate);
            await db.RejectAsync($"""
                INSERT INTO customers(id,tenant_id,name)
                VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}','   ')
                """, PostgresErrorCodes.CheckViolation);
            return;
        }

        var validCustomer = new Customer(PostgresFixture.TenantId, Guid.NewGuid(), "Valid parent");
        await Repository.CreateAsync(validCustomer);
        var contact = new CustomerContact(PostgresFixture.TenantId, Guid.NewGuid(), validCustomer.Id, "   ");
        Assert.Throws<DomainValidationException>(contact.Validate);
        await db.RejectAsync($"""
            INSERT INTO customer_contacts(id,tenant_id,customer_id,name)
            VALUES(gen_random_uuid(),'{PostgresFixture.TenantId}','{validCustomer.Id}','   ')
            """, PostgresErrorCodes.CheckViolation);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
