using System.Security.Cryptography;
using System.Text;
using Pairnets.Core.Hashing;

namespace Pairnets.Core.Crypto;

/// <summary>
/// The keyed tag an encrypted nest stores in place of a file's plain SHA-256 (docs/ENCRYPTION.md §2.4):
/// HMAC-SHA256(tag key, path id ‖ plain SHA-256), as 64 lower-case hex characters, so it has the same shape as a
/// <see cref="ContentHash"/> and <c>SyncDecision.Decide</c>, the nest's hash column and its base check work unchanged.
/// The nest never sees a plain-content hash, so it can't test "is this the well-known file X?", and the path id in it
/// means two identical files in different places don't look alike. Because it is built from the plain SHA-256 (kept in
/// the encrypted meta), a move or rename gets its new tag without reading the file again.
/// </summary>
public static class ContentTag
{
    public static string Of(ReadOnlySpan<byte> tagKey, string pathId, string plainSha256)
    {
        if (!ContentHash.IsValid(plainSha256))
            throw new ArgumentException("Not a SHA-256 hash.", nameof(plainSha256));
        var input = Encoding.ASCII.GetBytes(pathId + ":" + plainSha256);
        return ContentHash.ToHex(HMACSHA256.HashData(tagKey, input));
    }
}
