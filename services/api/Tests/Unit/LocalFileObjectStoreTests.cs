using System.Text;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.UnitTests;

public sealed class LocalFileObjectStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quoteengine-b057-tests", Guid.NewGuid().ToString("N"));
    private LocalFileObjectStore Store => new(root);

    [Fact]
    public async Task Put_and_open_read_return_identical_bytes()
    {
        var content = Encoding.UTF8.GetBytes("rfq-object-content");
        var hash = RfqFileSha256.Compute(content);
        await using var input = new MemoryStream(content);
        var stored = await Store.PutAsync(input, hash);
        await using var opened = await Store.OpenReadAsync(hash);
        using var copy = new MemoryStream();
        await opened!.CopyToAsync(copy);

        Assert.True(stored.Created);
        Assert.Equal(content.LongLength, stored.ByteSize);
        Assert.Equal(content, copy.ToArray());
        Assert.True(File.Exists(Path.Combine(root, hash[..2], hash.Substring(2, 2), hash)));
    }

    [Fact]
    public async Task Same_content_twice_creates_one_object()
    {
        var content = Encoding.UTF8.GetBytes("same-object");
        var hash = RfqFileSha256.Compute(content);
        var first = await PutAsync(content, hash);
        var second = await PutAsync(content, hash);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task Hash_mismatch_leaves_no_final_or_temporary_file()
    {
        var content = Encoding.UTF8.GetBytes("actual-content");
        var expected = RfqFileSha256.Compute(Encoding.UTF8.GetBytes("expected-content"));
        var error = await Assert.ThrowsAsync<FileObjectHashMismatchException>(() => PutAsync(content, expected));

        Assert.Equal(expected, error.ExpectedSha256);
        Assert.Equal(RfqFileSha256.Compute(content), error.ActualSha256);
        Assert.Empty(AllFiles());
        Assert.Null(await Store.OpenReadAsync(expected));
    }

    [Fact]
    public async Task Concurrent_puts_of_same_hash_create_one_correct_object()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 128_000));
        var hash = RfqFileSha256.Compute(content);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PutAsync(content, hash)));
        await using var opened = await Store.OpenReadAsync(hash);
        using var copy = new MemoryStream();
        await opened!.CopyToAsync(copy);

        Assert.Single(results, x => x.Created);
        Assert.Single(AllFiles());
        Assert.Equal(content, copy.ToArray());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("abc")]
    public async Task Invalid_hash_and_path_traversal_are_validation_errors(string hash)
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => PutAsync([1, 2, 3], hash));
        await Assert.ThrowsAsync<DomainValidationException>(() => Store.OpenReadAsync(hash));
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task Missing_object_returns_null()
    {
        var hash = RfqFileSha256.Compute(Encoding.UTF8.GetBytes("missing"));
        Assert.Null(await Store.OpenReadAsync(hash));
    }

    private async Task<FileObjectPutResult> PutAsync(byte[] content, string hash)
    {
        await using var stream = new MemoryStream(content, writable: false);
        return await Store.PutAsync(stream, hash);
    }

    private string[] AllFiles() => Directory.Exists(root)
        ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        : [];

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quoteengine-b057-tests"));
        if (resolved.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}
