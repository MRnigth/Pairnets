using System.Security.Cryptography;
using System.Text;

namespace Pairnets.Core.Client;

/// <summary>
/// The long code a sign-in link's browser page shows after Allow (cloud/CONTRACT.md §6.9, "Sign in with a link"):
/// 28 random characters of the user-code alphabet and a 4-character check, 32 in all, shown as 8 groups of 4. The check
/// is the first four bytes of SHA-256("pn-lc1\n" + the 28), each taken &amp; 31 into the alphabet, so a typo is caught
/// here before anything is sent (the service doesn't count a typo as a wrong try either).
/// </summary>
public static class LinkCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int RandomLength = 28;
    public const int Length = RandomLength + 4;

    /// <summary>
    /// The 32 characters without hyphens, as pasted or typed: spaces, tabs, line breaks and hyphens dropped, small letters
    /// made capitals, I and L read as 1 and O as 0. Null when that isn't 32 characters of the alphabet.
    /// </summary>
    public static string? Normalise(string? typed)
    {
        if (typed is null || typed.Length > 120)
            return null;
        var sb = new StringBuilder(Length);
        foreach (var ch in typed)
        {
            if (ch is ' ' or '\t' or '\r' or '\n' or '-')
                continue;
            var c = ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch;
            c = c switch { 'I' or 'L' => '1', 'O' => '0', _ => c };
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
                return null;
            sb.Append(c);
        }
        return sb.Length == Length ? sb.ToString() : null;
    }

    /// <summary>Whether a normalised code's last 4 characters are its check.</summary>
    public static bool Checks(string code) => code.Length == Length && Check(code[..RandomLength]) == code[RandomLength..];

    /// <summary>The normalised code if it is whole and its check adds up; else null (a typo).</summary>
    public static string? Read(string? typed) => Normalise(typed) is { } code && Checks(code) ? code : null;

    private static string Check(string random)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes("pn-lc1\n" + random));
        var s = new char[4];
        for (var i = 0; i < 4; i++)
            s[i] = Alphabet[digest[i] & 31];
        return new string(s);
    }
}
