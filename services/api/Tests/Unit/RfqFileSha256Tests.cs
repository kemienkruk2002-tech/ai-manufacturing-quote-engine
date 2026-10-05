using System.Text;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RfqFileSha256Tests
{
    [Fact]
    public async Task Same_content_has_same_lowercase_hash_and_different_content_changes_it()
    {
        var bytes = Encoding.UTF8.GetBytes("deterministic-rfq-content");
        var first = RfqFileSha256.Compute(bytes);
        var second = RfqFileSha256.Compute(bytes);
        await using var stream = new MemoryStream(bytes);
        var streamed = await RfqFileSha256.ComputeAsync(stream);
        var different = RfqFileSha256.Compute(Encoding.UTF8.GetBytes("different-rfq-content"));

        Assert.Equal(first, second);
        Assert.Equal(first, streamed);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(first, different);
    }
}
