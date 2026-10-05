using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Domain.Customers;

namespace QuoteEngine.Persistence;

public sealed class CustomerRepository(NpgsqlDataSource dataSource) : ICustomerRepository
{
    public async Task<Customer> CreateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        customer.Validate();

        await using var command = dataSource.CreateCommand("""
            INSERT INTO customers(id,tenant_id,name)
            VALUES(@id,@tenant,@name)
            RETURNING id,tenant_id,name
            """);
        command.Parameters.AddWithValue("id", customer.Id);
        command.Parameters.AddWithValue("tenant", customer.TenantId);
        command.Parameters.AddWithValue("name", customer.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Customer insert/read failed.");
        return ReadCustomer(reader);
    }

    public async Task<Customer?> FindAsync(Guid tenantId, Guid customerId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || customerId == Guid.Empty) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT id,tenant_id,name
            FROM customers
            WHERE tenant_id=@tenant AND id=@id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomer(reader) : null;
    }

    public async Task<CustomerContact> CreateContactAsync(CustomerContact contact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        contact.Validate();

        await using var command = dataSource.CreateCommand("""
            INSERT INTO customer_contacts(id,tenant_id,customer_id,name)
            VALUES(@id,@tenant,@customer,@name)
            RETURNING id,tenant_id,customer_id,name
            """);
        command.Parameters.AddWithValue("id", contact.Id);
        command.Parameters.AddWithValue("tenant", contact.TenantId);
        command.Parameters.AddWithValue("customer", contact.CustomerId);
        command.Parameters.AddWithValue("name", contact.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Customer contact insert/read failed.");
        return ReadContact(reader);
    }

    public async Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid contactId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || contactId == Guid.Empty) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT id,tenant_id,customer_id,name
            FROM customer_contacts
            WHERE tenant_id=@tenant AND id=@id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("id", contactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadContact(reader) : null;
    }

    public async Task<IReadOnlyList<CustomerContact>> ListContactsAsync(Guid tenantId, Guid customerId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || customerId == Guid.Empty) return [];

        await using var command = dataSource.CreateCommand("""
            SELECT id,tenant_id,customer_id,name
            FROM customer_contacts
            WHERE tenant_id=@tenant AND customer_id=@customer
            ORDER BY id
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("customer", customerId);

        var contacts = new List<CustomerContact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            contacts.Add(ReadContact(reader));
        return contacts.AsReadOnly();
    }

    private static Customer ReadCustomer(NpgsqlDataReader reader) =>
        new(reader.GetGuid(1), reader.GetGuid(0), reader.GetString(2));

    private static CustomerContact ReadContact(NpgsqlDataReader reader) =>
        new(reader.GetGuid(1), reader.GetGuid(0), reader.GetGuid(2), reader.GetString(3));
}
