namespace QuoteEngine.Domain.Calculation;

public enum CalculationStatus { Success, Blocked, Failed }

public sealed record ValidationIssue(string Code, string Message, string? OperationNo = null);

public sealed class DomainValidationException : Exception
{
    public IReadOnlyList<ValidationIssue> Errors { get; }

    public DomainValidationException(string code, string message)
        : this([new ValidationIssue(code, message)]) { }

    public DomainValidationException(IEnumerable<ValidationIssue> errors)
        : base(string.Join("; ", errors.Select(x => $"{x.Code}: {x.Message}")))
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }
}
