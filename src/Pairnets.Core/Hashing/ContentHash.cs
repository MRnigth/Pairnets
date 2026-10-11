using System.Security.Cryptography;

namespace Pairnets.Core.Hashing;

/// <summary>SHA-256 content hashing; hashes are lowercase hex strings.</summary>
public static class ContentHash
{
    /// <summary>The literal base value meaning "the file must not exist on the server".</summary>
    public const string NoneBase = "none";

    public const int BufferSize = 1024 * 1024;

    public static string ToHex(ReadOnlySpan<byte> digest) => Convert.ToHexString(digest).ToLowerInvariant();

    public static string Of(ReadOnlySpan<byte> data) => ToHex(SHA256.HashData(data));

    public static async Task<string> OfStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return ToHex(digest);
    }

    /// <summary>Hashes a file, opening it so that other programs can keep writing or deleting it.</summary>
    public static async Task<(string Hash, long Length)> OfFileAsync(string fullPath, CancellationToken ct = default)
    {
        await using var fs = OpenForSharedRead(fullPath);
        var hash = await OfStreamAsync(fs, ct).ConfigureAwait(false);
        return (hash, fs.Length);
    }

    /// <summary>Like <see cref="OfFileAsync(string, CancellationToken)"/>, telling <paramref name="progress"/> how many bytes were read so far.</summary>
    public static async Task<(string Hash, long Length)> OfFileAsync(string fullPath, Action<long>? progress, CancellationToken ct = default)
    {
        if (progress is null)
            return await OfFileAsync(fullPath, ct).ConfigureAwait(false);
        await using var fs = OpenForSharedRead(fullPath);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long read = 0;
            int n;
            while ((n = await fs.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                read += n;
                progress(read);
            }
            return (ToHex(sha.GetHashAndReset()), fs.Length);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Opens a file for reading with FileShare.ReadWrite | FileShare.Delete.</summary>
    public static FileStream OpenForSharedRead(string fullPath) =>
        new(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    public static bool IsValid(string? hash)
    {
        if (hash is null || hash.Length != 64)
            return false;
        foreach (var c in hash)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    /// <summary>First 8 hex characters, used in history file names.</summary>
    public static string Short(string hash) => hash.Length >= 8 ? hash[..8] : hash;
}
