using System.Security.Cryptography;
using System.Text;

namespace Pairnets.Core.Crypto;

/// <summary>
/// A nest's recovery key (docs/ENCRYPTION.md §2.7): 31 random base32 letters (155 bits) and one check letter, shown
/// once as eight groups of four (<c>ABCD-EFGH-…</c>). It is random rather than a password, so it needs no slow
/// hashing; it opens only the recovery envelope. Pairnets never gets a copy: lose every computer and this key, and
/// the files can't be opened by anyone.
/// </summary>
public sealed class RecoveryKey
{
    public const int Groups = 8;
    private const int DataLetters = 31;

    private readonly string _letters; // 32 lower-case base32 letters, the last one the check

    private RecoveryKey(string letters) => _letters = letters;

    public static RecoveryKey Create()
    {
        var data = Base32.Encode(RandomNumberGenerator.GetBytes(20))[..DataLetters];
        return new RecoveryKey(data + Check(data));
    }

    /// <summary>
    /// Reads a typed key: letter case, spaces and dashes don't matter, and 0, 1 and 8 count as O, I and B. Null when
    /// it isn't 32 letters or the check letter doesn't match (a typo).
    /// </summary>
    public static RecoveryKey? Parse(string? typed)
    {
        if (typed is null)
            return null;
        var sb = new StringBuilder(32);
        foreach (var c in typed)
        {
            if (c is ' ' or '-' or '\t')
                continue;
            sb.Append(c switch { '0' => 'o', '1' => 'i', '8' => 'b', _ => char.ToLowerInvariant(c) });
        }
        var letters = sb.ToString();
        if (letters.Length != DataLetters + 1 || Base32.Values(letters) is null)
            return null;
        return letters[DataLetters] == Check(letters[..DataLetters]) ? new RecoveryKey(letters) : null;
    }

    /// <summary>The key as the user writes it down: eight groups of four capital letters and digits.</summary>
    public override string ToString() =>
        string.Join("-", Enumerable.Range(0, Groups).Select(Group));

    /// <summary>Group <paramref name="index"/> (0–7), for "type groups 3 and 6 back" in the wizard.</summary>
    public string Group(int index) => _letters.Substring(index * 4, 4).ToUpperInvariant();

    /// <summary>The key that opens this nest's recovery envelope.</summary>
    internal byte[] EnvelopeKey(string nestId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes(_letters), 32,
            salt: Encoding.UTF8.GetBytes("pairnets/recovery/" + nestId), info: "pairnets/v1/recovery"u8.ToArray());

    private static char Check(string data) =>
        Base32.Alphabet[SHA256.HashData(Encoding.ASCII.GetBytes("pairnets-recovery:" + data))[0] & 31];
}
