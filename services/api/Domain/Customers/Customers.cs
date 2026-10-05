using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Domain.Customers;

public sealed record Customer(Guid TenantId, Guid Id, string Name)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || Id == Guid.Empty)
            throw new DomainValidationException("CUSTOMER_IDENTITY_INVALID",
                "Customer tenant and id are required.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainValidationException("CUSTOMER_NAME_INVALID",
                "Customer name is required.");
    }
}

public sealed record CustomerContact(Guid TenantId, Guid Id, Guid CustomerId, string Name)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || Id == Guid.Empty || CustomerId == Guid.Empty)
            throw new DomainValidationException("CUSTOMER_CONTACT_IDENTITY_INVALID",
                "Contact tenant, id and customer are required.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainValidationException("CUSTOMER_CONTACT_NAME_INVALID",
                "Contact name is required.");
    }
}
