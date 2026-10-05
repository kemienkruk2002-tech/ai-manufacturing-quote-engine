using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Domain.Machines;

public enum RateType { Tj, Tpz, Overhead, Labor, Other }

public sealed record Machine(Guid TenantId, Guid Id, string Code, string Name, bool Active = true);

public sealed record MachineRate(Guid TenantId, Guid Id, Guid MachineId, RateType RateType,
    decimal RatePlnPerHour, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
    string RateVersion, string? SourceReference = null);

/// <summary>Uses a supplied instant and half-open [from,to) intervals; never reads a clock.</summary>
public static class MachineRateResolver
{
    public static MachineRate Resolve(IEnumerable<MachineRate> rates, Guid tenantId,
        Guid machineId, RateType rateType, DateTimeOffset at)
    {
        var matches = rates.Where(x => x.TenantId == tenantId && x.MachineId == machineId
            && x.RateType == rateType && x.EffectiveFrom <= at
            && (x.EffectiveTo is null || at < x.EffectiveTo)).ToArray();
        if (matches.Length == 0)
            throw new DomainValidationException("MACHINE_RATE_MISSING", "No effective machine rate is available; cost calculation is blocked.");
        if (matches.Length > 1)
            throw new DomainValidationException("MACHINE_RATE_AMBIGUOUS", "More than one machine rate applies to the supplied instant.");
        if (matches[0].RatePlnPerHour < 0)
            throw new DomainValidationException("MACHINE_RATE_NEGATIVE", "A machine rate cannot be negative.");
        return matches[0];
    }
}
