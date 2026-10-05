namespace QuoteEngine.Application;

public interface ITenantContext
{
    Guid TenantId { get; }
}

public sealed class TenantContextUnavailableException(string message) : Exception(message);

public sealed class TenantResourceMismatchException()
    : Exception("The requested resource does not belong to the active tenant context.");

public static class TenantContextGuard
{
    public static Guid RequireRouteTenant(this ITenantContext tenantContext, Guid routeTenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        var trustedTenantId = tenantContext.TenantId;
        if (trustedTenantId == Guid.Empty)
            throw new TenantContextUnavailableException("Tenant identity is unavailable.");
        if (trustedTenantId != routeTenantId)
            throw new TenantResourceMismatchException();
        return trustedTenantId;
    }
}
