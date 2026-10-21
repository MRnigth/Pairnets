using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pairnets.Core.Crypto;

/// <summary>
/// What an encrypted nest's manifest row hides (docs/ENCRYPTION.md §2.3): the real path, the plain size, the modified
/// time, the plain SHA-256 (so a move gets its new tag without reading the file) and the hash of the blob's
/// <see cref="SealedHeader"/> (so the nest can't swap two files' blobs).
/// </summary>
public sealed record FileMeta(string Path, long Size, long ModifiedUnixMs, string PlainSha256, string HeaderHash);

/// <summary>
/// Encrypts <see cref="FileMeta"/> for the nest's opaque <c>meta</c> column: "PNM1", the generation (4 bytes,
/// big-endian), a 12-byte nonce, then AES-256-GCM of the JSON padded to a multiple of 64 bytes (so the length gives
/// little away about the name) and its tag. The associated data is the first 8 bytes and the path id, so a meta blob
/// can't be moved to another row. The other computers read paths from it without downloading files.
/// </summary>
public static class MetaBlob
{
    private const int Pad = 64;
    private static ReadOnlySpan<byte> Magic => "PNM1"u8;

    public static string Seal(KeyRing ring, string pathId, FileMeta meta)
    {
        var generation = ring.Current;
        var json = JsonSerializer.SerializeToUtf8Bytes(meta, Json);
        var plain = new byte[(json.Length / Pad + 1) * Pad];
        json.CopyTo(plain, 0);
        plain.AsSpan(json.Length).Fill((byte)' ');
        var blob = new byte[8 + 12 + plain.Length + 16];
        Magic.CopyTo(blob);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4, 4), generation);
        RandomNumberGenerator.Fill(blob.AsSpan(8, 12));
        using var aes = new AesGcm(ring.MetaKey(generation), 16);
        aes.Encrypt(blob.AsSpan(8, 12), plain, blob.AsSpan(20, plain.Length), blob.AsSpan(20 + plain.Length, 16),
            Bound(blob, pathId));
        return Convert.ToBase64String(blob);
    }

    /// <summary>Opens a meta blob; throws <see cref="CryptographicException"/> if it was changed or belongs to another row.</summary>
    public static FileMeta Open(KeyRing ring, string pathId, string sealedMeta)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(sealedMeta);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("A file's details are damaged.", ex);
        }
        if (blob.Length < 8 + 12 + 16 || !blob.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("A file's details are damaged.");
        var generation = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4, 4));
        var plain = new byte[blob.Length - 8 - 12 - 16];
        using (var aes = new AesGcm(ring.MetaKey(generation), 16))
            aes.Decrypt(blob.AsSpan(8, 12), blob.AsSpan(20, plain.Length), blob.AsSpan(20 + plain.Length, 16), plain,
                Bound(blob, pathId));
        try
        {
            return JsonSerializer.Deserialize<FileMeta>(plain, Json)
                ?? throw new CryptographicException("A file's details are damaged.");
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("A file's details are damaged.", ex);
        }
    }

    private static byte[] Bound(byte[] blob, string pathId)
    {
        var id = Encoding.ASCII.GetBytes(pathId);
        var aad = new byte[8 + id.Length];
        blob.AsSpan(0, 8).CopyTo(aad);
        id.CopyTo(aad, 8);
        return aad;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
