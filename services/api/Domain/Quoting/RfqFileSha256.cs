using System.Security.Cryptography;

namespace QuoteEngine.Domain.Quoting;

public static class RfqFileSha256
{
    public static string Compute(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string Compute(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(content)).ToLowerInvariant();
    }

    public static async Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var algorithm = SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(content, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
