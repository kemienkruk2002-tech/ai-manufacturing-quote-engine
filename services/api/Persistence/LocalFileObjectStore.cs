using System.Buffers;
using System.Security.Cryptography;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Persistence;

public sealed class LocalFileObjectStore : IFileObjectStore
{
    private static readonly SemaphoreSlim[] PublicationGates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly string rootPath;

    public LocalFileObjectStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Object store root path is required.", nameof(rootPath));
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<FileObjectPutResult> PutAsync(Stream content, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(content));
        ValidateHash(expectedSha256);

        var finalPath = PathFor(expectedSha256);
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{expectedSha256}.{Guid.NewGuid():N}.tmp");
        var byteSize = 0L;
        string actualSha256;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        byteSize = checked(byteSize + read);
                    }
                    await destination.FlushAsync(cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(actualSha256, expectedSha256))
                throw new FileObjectHashMismatchException(expectedSha256, actualSha256);

            // Keep publication atomic: only a fully written, hash-verified temporary file is renamed
            // to the content-addressed path. The striped in-process gate provides deterministic
            // winner election across LocalFileObjectStore instances without exposing partial bytes.
            var publicationGate = PublicationGates[Convert.ToInt32(expectedSha256[..2], 16) % PublicationGates.Length];
            await publicationGate.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(finalPath))
                    return new(expectedSha256, byteSize, false);

                File.Move(temporaryPath, finalPath, false);
                return new(expectedSha256, byteSize, true);
            }
            finally
            {
                publicationGate.Release();
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateHash(sha256);
        var path = PathFor(sha256);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    private string PathFor(string sha256) =>
        Path.Combine(rootPath, sha256[..2], sha256.Substring(2, 2), sha256);

    private static void ValidateHash(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new DomainValidationException("FILE_OBJECT_HASH_INVALID", "SHA-256 must be lowercase 64-hex.");
    }
}
