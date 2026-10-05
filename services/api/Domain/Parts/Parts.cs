namespace QuoteEngine.Domain.Parts;

public enum PartRevisionStatus { Draft, Active, Archived }

public sealed record Part(Guid TenantId, Guid Id, string PartNumber, string Name, string DefaultUnit = "szt");

public sealed record PartRevision(Guid TenantId, Guid Id, Guid PartId, string? RevisionCode,
    string? Variant, string? ProcessVersion, PartRevisionStatus Status, decimal? FinalMassKg,
    string? SourceReference = null);
