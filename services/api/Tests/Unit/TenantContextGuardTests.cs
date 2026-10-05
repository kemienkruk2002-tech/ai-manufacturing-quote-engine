using QuoteEngine.Application;

namespace QuoteEngine.UnitTests;

public sealed class TenantContextGuardTests
{
    [Fact]
    public void Matching_route_tenant_returns_trusted_tenant()
    {
        var tenantId = Guid.NewGuid();
        ITenantContext context = new TestTenantContext(tenantId);

        var result = context.RequireRouteTenant(tenantId);

        Assert.Equal(tenantId, result);
    }

    [Fact]
    public void Different_route_tenant_is_rejected()
    {
        ITenantContext context = new TestTenantContext(Guid.NewGuid());

        Assert.Throws<TenantResourceMismatchException>(() =>
            context.RequireRouteTenant(Guid.NewGuid()));
    }

    [Fact]
    public void Empty_trusted_tenant_is_rejected()
    {
        ITenantContext context = new TestTenantContext(Guid.Empty);

        Assert.Throws<TenantContextUnavailableException>(() =>
            context.RequireRouteTenant(Guid.NewGuid()));
    }

    private sealed class TestTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
