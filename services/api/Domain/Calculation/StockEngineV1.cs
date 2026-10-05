using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Domain.Calculation;

public sealed record StockCalculationInput(decimal? FinalMassKg, StockSnapshot? Stock);

public sealed class StockCalculationResult
{
    public CalculationStatus Status { get; }
    public IReadOnlyList<ValidationIssue> Errors { get; }
    public decimal? FinalMassKg { get; }
    public decimal? NormMassKgPerUnit { get; }
    public decimal? MaterialUtilization { get; }
    public decimal? ScrapFraction { get; }
    public string EngineVersion => StockEngineV1.Version;

    internal StockCalculationResult(CalculationStatus status, IEnumerable<ValidationIssue> errors,
        decimal? finalMassKg, decimal? normMassKgPerUnit, decimal? materialUtilization = null,
        decimal? scrapFraction = null)
    {
        Status = status;
        Errors = Array.AsReadOnly(errors.ToArray());
        FinalMassKg = finalMassKg;
        NormMassKgPerUnit = normMassKgPerUnit;
        MaterialUtilization = materialUtilization;
        ScrapFraction = scrapFraction;
    }
}

public sealed class StockEngineV1
{
    public const string Version = "stock-engine-v1";

    public StockCalculationResult Calculate(StockCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<ValidationIssue>();
        if (input.FinalMassKg is null)
            errors.Add(new("FINAL_MASS_MISSING", "Final mass is required; it cannot be inferred from stock norm mass."));
        else if (input.FinalMassKg < 0)
            errors.Add(new("FINAL_MASS_NEGATIVE", "Final mass cannot be negative."));
        if (input.Stock is null)
            errors.Add(new("STOCK_MISSING", "An approved stock definition is required."));
        else
        {
            if (input.Stock.ApprovalStatus != ApprovalStatus.Approved)
                errors.Add(new("STOCK_NOT_APPROVED", "The stock definition must be approved."));
            if (input.Stock.NormMassKgPerUnit is null)
                errors.Add(new("STOCK_NORM_MISSING", "Approved norm mass is required; it cannot be inferred from final mass."));
            else if (input.Stock.NormMassKgPerUnit <= 0)
                errors.Add(new("STOCK_NORM_INVALID", "Approved norm mass must be greater than zero."));
        }
        if (errors.Count > 0)
            return new(CalculationStatus.Blocked, errors, input.FinalMassKg, input.Stock?.NormMassKgPerUnit);

        // Apply the supplied formula without clamping or an invented final-mass/norm policy.
        // TODO(business): decide how a confirmed final mass exceeding norm should be reviewed.
        var utilization = input.FinalMassKg!.Value / input.Stock!.NormMassKgPerUnit!.Value;
        return new(CalculationStatus.Success, [], input.FinalMassKg, input.Stock.NormMassKgPerUnit,
            utilization, 1m - utilization);
    }
}
