using System.Security.Cryptography;

namespace Tether.Core.Hashing;

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
