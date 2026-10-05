using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Domain.Calculation;

public sealed record CostCalculationInput(RouteSnapshot? Route, int Quantity, CostInputsSnapshot? Cost);

public sealed record CostOperationTrace(string OperationNo, int SequenceNo, decimal TjSec,
    decimal TpzMinPerBatch, int Quantity, decimal RateOverallPlnH, decimal RateTpzPlnH,
    string TjFormula, string TpzFormula, decimal TjCostUnit, decimal TpzCostUnit,
    decimal LaborCostUnit);

public sealed record CostCalculationTrace(decimal MaterialCostUnit, string Currency,
    string MachineRateVersion, string CostEngineVersion, string LaborTjFormula,
    string LaborTpzFormula, string LaborFormula, string TotalFormula,
    IReadOnlyList<CostOperationTrace> Operations);

public sealed class CostCalculationResult
{
    public CalculationStatus Status { get; }
    public IReadOnlyList<ValidationIssue> Errors { get; }
    public int Quantity { get; }
    public decimal? MaterialCostUnit { get; }
    public decimal? LaborTjCostUnit { get; }
    public decimal? LaborTpzCostUnit { get; }
    public decimal? LaborCostUnit { get; }
    public decimal? TotalCostUnit { get; }
    public IReadOnlyList<CostOperationTrace> OperationResults { get; }
    public CostCalculationTrace? Trace { get; }
    public string EngineVersion => CostEngineV1.Version;

    internal CostCalculationResult(CalculationStatus status, int quantity,
        IEnumerable<ValidationIssue> errors, decimal? materialCostUnit = null,
        decimal? laborTjCostUnit = null, decimal? laborTpzCostUnit = null,
        decimal? laborCostUnit = null, decimal? totalCostUnit = null,
        IEnumerable<CostOperationTrace>? operationResults = null, CostCalculationTrace? trace = null)
    {
        Status = status;
        Quantity = quantity;
        Errors = Array.AsReadOnly(errors.ToArray());
        MaterialCostUnit = materialCostUnit;
        LaborTjCostUnit = laborTjCostUnit;
        LaborTpzCostUnit = laborTpzCostUnit;
        LaborCostUnit = laborCostUnit;
        TotalCostUnit = totalCostUnit;
        OperationResults = Array.AsReadOnly((operationResults ?? []).OrderBy(x => x.SequenceNo)
            .ThenBy(x => x.OperationNo, StringComparer.Ordinal).ToArray());
        Trace = trace;
    }
}

/// <summary>Pure decimal cost calculation over a complete canonical snapshot.</summary>
public sealed class CostEngineV1
{
    public const string Version = "cost-engine-v1";

    public CostCalculationResult Calculate(CostCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Quantity <= 0)
            throw new DomainValidationException("QUANTITY_INVALID", "Quantity must be greater than zero.");

        var errors = RouteValidator.Validate(input.Route).ToList();
        var invalidRoute = errors.Where(x => x.Code is "TJ_NEGATIVE" or "TPZ_NEGATIVE"
            or "DUPLICATE_OPERATION_NO" or "DUPLICATE_SEQUENCE_NO" or "SEQUENCE_NO_INVALID").ToArray();
        if (invalidRoute.Length > 0) throw new DomainValidationException(invalidRoute);

        if (input.Cost is null)
            errors.Add(new("COST_INPUT_MISSING", "Cost snapshot input is required."));
        else
        {
            if (input.Cost.MaterialCostUnit is null)
                errors.Add(new("MATERIAL_COST_MISSING", "Material cost per unit is required."));
            else if (input.Cost.MaterialCostUnit < 0)
                throw new DomainValidationException("MATERIAL_COST_NEGATIVE", "Material cost per unit cannot be negative.");
            if (!StringComparer.Ordinal.Equals(input.Cost.CostEngineVersion, Version))
                errors.Add(new("COST_ENGINE_VERSION_UNSUPPORTED", "The cost snapshot requires an unsupported engine version."));
            foreach (var rate in input.Cost.OperationRates)
            {
                if (rate.RateTpzPlnH < 0 || rate.RateProductionPlnH < 0 || rate.RateOverallPlnH < 0)
                    throw new DomainValidationException("RATE_NEGATIVE", "Machine rates cannot be negative.");
            }
        }

        if (input.Route is not null && input.Cost is not null)
        {
            var duplicateRates = input.Cost.OperationRates.GroupBy(x => x.OperationNo, StringComparer.Ordinal)
                .Where(x => x.Count() > 1).Select(x => new ValidationIssue("RATE_AMBIGUOUS",
                    "More than one rate applies to the operation.", x.Key));
            errors.AddRange(duplicateRates);
            var byOperation = input.Cost.OperationRates.GroupBy(x => x.OperationNo, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            foreach (var operation in input.Route.Operations)
            {
                if (!byOperation.TryGetValue(operation.OperationNo, out var rate))
                    errors.Add(new("RATE_MISSING", "Required machine rates are missing.", operation.OperationNo));
                else
                {
                    if (rate.RateOverallPlnH is null)
                        errors.Add(new("RATE_OVERALL_MISSING", "Overall hourly rate is required for Tj cost.", operation.OperationNo));
                    if (rate.RateTpzPlnH is null)
                        errors.Add(new("RATE_TPZ_MISSING", "Tpz hourly rate is required for setup cost.", operation.OperationNo));
                    if (operation.Machine is not null && !StringComparer.Ordinal.Equals(rate.MachineCode, operation.Machine.Code))
                        errors.Add(new("RATE_MACHINE_MISMATCH", "The rate belongs to a different machine.", operation.OperationNo));
                }
            }
        }
        if (errors.Count > 0)
            return new(CalculationStatus.Blocked, input.Quantity, errors, input.Cost?.MaterialCostUnit);

        var rates = input.Cost!.OperationRates.ToDictionary(x => x.OperationNo, StringComparer.Ordinal);
        var operationResults = input.Route!.Operations.Select(operation =>
        {
            var rate = rates[operation.OperationNo];
            var tj = operation.TjSec!.Value;
            var tpz = operation.TpzMinPerBatch!.Value;
            var overall = rate.RateOverallPlnH!.Value;
            var tpzRate = rate.RateTpzPlnH!.Value;
            var tjCost = tj / 3600m * overall;
            var tpzCost = (tpz / input.Quantity) / 60m * tpzRate;
            return new CostOperationTrace(operation.OperationNo, operation.SequenceNo, tj, tpz,
                input.Quantity, overall, tpzRate,
                "tj_sec / 3600 * rate_overall_pln_h",
                "(tpz_min_per_batch / quantity) / 60 * rate_tpz_pln_h",
                tjCost, tpzCost, tjCost + tpzCost);
        }).ToArray();
        var laborTj = operationResults.Sum(x => x.TjCostUnit);
        var laborTpz = operationResults.Sum(x => x.TpzCostUnit);
        var labor = laborTj + laborTpz;
        var material = input.Cost.MaterialCostUnit!.Value;
        var trace = new CostCalculationTrace(material, input.Cost.Currency,
            input.Cost.MachineRateVersion, input.Cost.CostEngineVersion,
            "sum(operation.tj_cost_unit)", "sum(operation.tpz_cost_unit)",
            "labor_tj_cost_unit + labor_tpz_cost_unit",
            "material_cost_unit + labor_cost_unit", Array.AsReadOnly(operationResults));
        return new(CalculationStatus.Success, input.Quantity, [], material, laborTj, laborTpz,
            labor, material + labor, operationResults, trace);
    }
}

public static class CostPresentation
{
    public static decimal RoundMoney(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static CostDisplayResult Project(CostCalculationResult result)
    {
        if (result.Status != CalculationStatus.Success)
            throw new InvalidOperationException("Only a successful cost result can be projected for display.");
        var operations = result.OperationResults.Select(operation =>
        {
            var tj = RoundMoney(operation.TjCostUnit);
            var tpz = RoundMoney(operation.TpzCostUnit);
            return new CostOperationDisplay(operation.OperationNo, tj, tpz, tj + tpz);
        }).ToArray();
        var laborTj = RoundMoney(result.LaborTjCostUnit!.Value);
        var laborTpz = RoundMoney(result.LaborTpzCostUnit!.Value);
        var labor = laborTj + laborTpz;
        var material = RoundMoney(result.MaterialCostUnit!.Value);
        return new CostDisplayResult(material, laborTj, laborTpz, labor, material + labor,
            Array.AsReadOnly(operations));
    }
}

public sealed record CostOperationDisplay(string OperationNo, decimal TjCostUnit,
    decimal TpzCostUnit, decimal LaborCostUnit);

public sealed record CostDisplayResult(decimal MaterialCostUnit, decimal LaborTjCostUnit,
    decimal LaborTpzCostUnit, decimal LaborCostUnit, decimal TotalCostUnit,
    IReadOnlyList<CostOperationDisplay> Operations);
