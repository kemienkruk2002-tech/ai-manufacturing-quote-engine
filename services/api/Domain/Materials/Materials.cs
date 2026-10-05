using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Domain.Materials;

public enum StockType { RoundBar, FlatBar, Plate, Tube, Forging, Casting, Other }
public enum StockSourceType { Document, MasterData, Manual, Calculated }

public sealed record Material(Guid TenantId, Guid Id, string Code, string Grade, string DisplayName,
    decimal? DensityKgM3 = null, string? SourceReference = null);

public sealed record StockDefinition(Guid TenantId, Guid Id, Guid MaterialId, StockType Type,
    decimal? DiameterMm, decimal? NormMassKgPerUnit, StockSourceType SourceType,
    ApprovalStatus ApprovalStatus, string? SourceReference = null, string? RuleVersion = null);
