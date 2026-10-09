namespace Pairnets.Server.Auth;

/// <summary>
/// Strict base64url (cloud/CONTRACT.md §1.7), for everything the Pairnets service sends: only <c>A-Z a-z 0-9 - _</c>,
/// never padded, a length that is not 1 more than a multiple of 4, and it must encode back to exactly the same text
/// (so unused bits are zero and no two strings stand for the same bytes). The lenient decoder of the passkey code
/// accepts padding and must not be used for these.
/// </summary>
public static class StrictBase64Url
{
    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null || text.Length % 4 == 1)
            return false;
        foreach (var c in text)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        }
        catch (FormatException)
        {
            return false;
        }
        if (!string.Equals(Encode(decoded), text, StringComparison.Ordinal))
            return false;
        bytes = decoded;
        return true;
    }

    /// <summary>True when <paramref name="text"/> is strict base64url of exactly <paramref name="length"/> bytes.</summary>
    public static bool IsBytes(string? text, int length) => TryDecode(text, out var bytes) && bytes.Length == length;

    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
