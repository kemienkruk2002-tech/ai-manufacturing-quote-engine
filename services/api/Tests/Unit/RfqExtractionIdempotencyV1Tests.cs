using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.UnitTests;

public sealed class RfqExtractionIdempotencyV1Tests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RfqId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Same_request_with_different_source_order_has_same_versioned_hash()
    {
        var first = RfqExtractionIdempotencyV1.Create("retry-1", TenantId, RfqId, "model-a", true,
        [
            new("drawing", 2, "pdf"),
            new("step", 1, "step")
        ]);
        var second = RfqExtractionIdempotencyV1.Create("retry-1", TenantId, RfqId, "model-a", true,
        [
            new("step", 1, "step"),
            new("drawing", 2, "pdf")
        ]);

        Assert.Equal(RfqExtractionIdempotencyV1.Version, first.Version);
        Assert.Equal(first.RequestHash, second.RequestHash);
        Assert.Equal(64, first.RequestHash.Length);
    }

    [Theory]
    [InlineData(false, "model-a", "pdf")]
    [InlineData(true, "model-b", "pdf")]
    [InlineData(true, "model-a", "step")]
    public void Material_request_change_changes_hash(bool allowExternalAi, string modelId, string documentType)
    {
        var baseline = RfqExtractionIdempotencyV1.Create("retry-1", TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "pdf")]);
        var changed = RfqExtractionIdempotencyV1.Create("retry-1", TenantId, RfqId, modelId, allowExternalAi,
            [new("drawing", 1, documentType)]);

        var requestActuallyChanged = allowExternalAi != true
            || !string.Equals(modelId, "model-a", StringComparison.Ordinal)
            || !string.Equals(documentType, "pdf", StringComparison.Ordinal);

        Assert.True(requestActuallyChanged, "Test data must change at least one hashed request field.");
        Assert.NotEqual(baseline.RequestHash, changed.RequestHash);
    }

    [Fact]
    public void Invalid_key_is_rejected()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            RfqExtractionIdempotencyV1.Create(" ", TenantId, RfqId, "model-a", true, []));

        Assert.Contains(error.Errors, issue => issue.Code == "RFQ_EXTRACTION_IDEMPOTENCY_KEY_INVALID");
    }
}
