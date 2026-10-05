using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Machines;

namespace QuoteEngine.UnitTests;

public sealed class MachineRateResolverTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid MachineId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Boundary = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Supplied_instant_resolves_half_open_effective_windows_unambiguously()
    {
        MachineRate[] rates =
        [
            new(TenantId, Guid.Empty, MachineId, RateType.Tj, 100m, Boundary.AddDays(-1), Boundary, "rates-v1"),
            new(TenantId, Guid.Empty, MachineId, RateType.Tj, 120m, Boundary, null, "rates-v2"),
            new(TenantId, Guid.Empty, MachineId, RateType.Tpz, 200m, Boundary, null, "rates-v2")
        ];
        Assert.Equal(100m, MachineRateResolver.Resolve(rates, TenantId, MachineId, RateType.Tj, Boundary.AddTicks(-1)).RatePlnPerHour);
        Assert.Equal(120m, MachineRateResolver.Resolve(rates, TenantId, MachineId, RateType.Tj, Boundary).RatePlnPerHour);
        Assert.Equal(200m, MachineRateResolver.Resolve(rates, TenantId, MachineId, RateType.Tpz, Boundary).RatePlnPerHour);
        Assert.Equal(120m, MachineRateResolver.Resolve(rates, TenantId, MachineId, RateType.Tj, Boundary.ToOffset(TimeSpan.FromHours(2))).RatePlnPerHour);
    }

    [Fact]
    public void Missing_or_ambiguous_rates_are_explicit_errors_without_zero_fallback()
    {
        var missing = Assert.Throws<DomainValidationException>(() =>
            MachineRateResolver.Resolve([], TenantId, MachineId, RateType.Tj, Boundary));
        Assert.Contains(missing.Errors, x => x.Code == "MACHINE_RATE_MISSING");
        var rate = new MachineRate(TenantId, Guid.Empty, MachineId, RateType.Tj, 100m, Boundary, null, "rates-v1");
        var ambiguous = Assert.Throws<DomainValidationException>(() =>
            MachineRateResolver.Resolve([rate, rate with { RateVersion = "rates-v2" }], TenantId, MachineId, RateType.Tj, Boundary));
        Assert.Contains(ambiguous.Errors, x => x.Code == "MACHINE_RATE_AMBIGUOUS");
    }
}
