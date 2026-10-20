using System.Security.Cryptography;
using System.Text;

namespace Pairnets.Core.Crypto;

/// <summary>
/// The name an encrypted nest stores a file under (docs/ENCRYPTION.md §2.3): the first 160 bits of
/// HMAC-SHA256(id key, NFC(path)) in lower-case base32, 32 characters, sharded by its first two. The same path always
/// gives the same id, so the nest's per-path base check, history and <c>since</c> manifest keep working; the nest
/// sees no names, no folders and no depth. Lower-case base32 never trips the nest's own letter-case check.
/// </summary>
public static class PathId
{
    public const int Length = 32;

    /// <summary>The id of <paramref name="relativePath"/> (the sync engine's forward-slash relative path).</summary>
    public static string Of(ReadOnlySpan<byte> idKey, string relativePath)
    {
        var normalized = relativePath.Normalize(NormalizationForm.FormC);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(idKey, Encoding.UTF8.GetBytes(normalized), mac);
        return Base32.Encode(mac[..20]);
    }

    /// <summary>The path the nest stores the blob at: <c>ab/abcdef…</c>.</summary>
    public static string StoragePath(string id) => id[..2] + "/" + id;

    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != Length)
            return false;
        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= '2' and <= '7'))
                return false;
        }
        return true;
    }
}

/// <summary>RFC 4648 base32 without padding, in lower case.</summary>
internal static class Base32
{
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>The 5-bit values of a base32 string, or null if a character isn't base32.</summary>
    public static byte[]? Values(string text)
    {
        var values = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var index = Alphabet.IndexOf(char.ToLowerInvariant(text[i]));
            if (index < 0)
                return null;
            values[i] = (byte)index;
        }
        return values;
    }
}
