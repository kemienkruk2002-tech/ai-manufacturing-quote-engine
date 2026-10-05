using System.Security.Claims;
using QuoteEngine.Application;

namespace QuoteEngine.Api;

internal sealed class ClaimsTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public const string TenantClaimType = "tenant_id";

    public Guid TenantId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                throw new TenantContextUnavailableException("Authenticated tenant identity is required.");

            var values = user.FindAll(TenantClaimType).Select(claim => claim.Value).ToArray();
            if (values.Length != 1 || !Guid.TryParse(values[0], out var tenantId) || tenantId == Guid.Empty)
                throw new TenantContextUnavailableException("Exactly one valid tenant_id claim is required.");

            return tenantId;
        }
    }
}
