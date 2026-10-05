using QuoteEngine.Domain.Quoting;
using QuoteEngine.Domain.Routing;

namespace QuoteEngine.Domain.Calculation;

public sealed record TimeCalculationInput(RouteSnapshot? Route, int Quantity);

public sealed record TimeOperationResult(string OperationNo, int SequenceNo, decimal UnitTjSec,
    decimal BatchTpzMin, decimal UnitTpzSec, decimal UnitLaborSec);

public sealed class TimeCalculationResult
{
    public CalculationStatus Status { get; }
    public IReadOnlyList<ValidationIssue> Errors { get; }
    public int Quantity { get; }
    public decimal? SumTjSecPerUnit { get; }
    public decimal? SumTpzMinPerBatch { get; }
    public decimal? SumTpzSecPerUnit { get; }
    public decimal? LaborSecPerUnit { get; }
    public decimal? LaborHoursBatch { get; }
    public IReadOnlyList<TimeOperationResult> OperationResults { get; }
    public string EngineVersion { get; }

    internal TimeCalculationResult(CalculationStatus status, int quantity, IEnumerable<ValidationIssue> errors,
        decimal? sumTjSecPerUnit = null, decimal? sumTpzMinPerBatch = null,
        decimal? sumTpzSecPerUnit = null, decimal? laborSecPerUnit = null,
        decimal? laborHoursBatch = null, IEnumerable<TimeOperationResult>? operationResults = null)
    {
        Status = status;
        Quantity = quantity;
        Errors = Array.AsReadOnly(errors.ToArray());
        SumTjSecPerUnit = sumTjSecPerUnit;
        SumTpzMinPerBatch = sumTpzMinPerBatch;
        SumTpzSecPerUnit = sumTpzSecPerUnit;
        LaborSecPerUnit = laborSecPerUnit;
        LaborHoursBatch = laborHoursBatch;
        OperationResults = Array.AsReadOnly((operationResults ?? []).OrderBy(x => x.SequenceNo)
            .ThenBy(x => x.OperationNo, StringComparer.Ordinal).ToArray());
        EngineVersion = TimeEngineV1.Version;
    }
}

/// <summary>Pure decimal arithmetic over a supplied immutable route; no I/O, clock or rates.</summary>
public sealed class TimeEngineV1
{
    public const string Version = "time-engine-v1";

    public TimeCalculationResult Calculate(TimeCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Quantity <= 0)
            throw new DomainValidationException("QUANTITY_INVALID", "Quantity must be greater than zero.");
        var errors = RouteValidator.Validate(input.Route);
        var invalidValues = errors.Where(x => x.Code is "TJ_NEGATIVE" or "TPZ_NEGATIVE"
            or "DUPLICATE_OPERATION_NO" or "DUPLICATE_SEQUENCE_NO" or "SEQUENCE_NO_INVALID").ToArray();
        if (invalidValues.Length > 0)
            throw new DomainValidationException(invalidValues);
        if (errors.Count > 0)
            return new(CalculationStatus.Blocked, input.Quantity, errors);

        var operations = input.Route!.Operations;
        var results = operations.Select(operation =>
        {
            var unitTj = operation.TjSec!.Value;
            var batchTpz = operation.TpzMinPerBatch!.Value;
            var unitTpz = batchTpz * 60m / input.Quantity;
            return new TimeOperationResult(operation.OperationNo, operation.SequenceNo,
                unitTj, batchTpz, unitTpz, unitTj + unitTpz);
        }).ToArray();

        var sumTj = operations.Sum(x => x.TjSec!.Value);
        var sumTpz = operations.Sum(x => x.TpzMinPerBatch!.Value);
        var sumTpzUnit = sumTpz * 60m / input.Quantity;
        var laborUnit = sumTj + sumTpzUnit;
        var laborHoursBatch = laborUnit * input.Quantity / 3600m;
        return new(CalculationStatus.Success, input.Quantity, [], sumTj, sumTpz,
            sumTpzUnit, laborUnit, laborHoursBatch, results);
    }
}
