namespace QuoteEngine.Domain.Audit;

public sealed record AuditEvent(Guid TenantId, Guid Id, string EntityType, Guid EntityId,
    string Action, string? OldValueJson, string? NewValueJson, Guid? UserId,
    string Source, DateTimeOffset CreatedAt);
