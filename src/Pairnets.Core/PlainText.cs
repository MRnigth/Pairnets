using System.Globalization;
using System.Text;

namespace Pairnets.Core;

/// <summary>
/// Cleans text that someone else chose (a computer's name) before it is stored or shown. Control, format and bidi
/// characters are removed: they are invisible or flip the reading direction, so a stranger could make a name look like
/// a real computer's ("LAPTOP-2" with a hidden character is not "LAPTOP-2"). The nest cleans names when they arrive and
/// the apps clean again when they show one, so an old nest cannot slip one through.
/// </summary>
public static class PlainText
{
    /// <summary>
    /// Invisible and direction-changing characters that are removed whatever the Unicode tables of this runtime say:
    /// soft hyphen, Arabic letter mark, Mongolian vowel separator, zero-width and direction marks, bidi embeddings and
    /// isolates, word joiner and invisible operators, byte order mark.
    /// </summary>
    private static bool IsHidden(Rune r)
    {
        var v = r.Value;
        if (v is 0x00AD or 0x061C or 0x180E or 0xFEFF or 0x2028 or 0x2029 || v is >= 0x200B and <= 0x200F
            || v is >= 0x202A and <= 0x202E || v is >= 0x2060 and <= 0x2064 || v is >= 0x2066 and <= 0x2069)
            return true;
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>
    /// <paramref name="text"/> without control, format and bidi characters, trimmed and cut to at most
    /// <paramref name="maxLength"/> characters (never in the middle of a character). Empty when nothing is left.
    /// </summary>
    public static string Clean(string? text, int maxLength = 64)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (!IsHidden(rune))
                sb.Append(rune.ToString());
        var clean = sb.ToString().Trim();
        if (clean.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(clean[cut - 1]))
                cut--;
            clean = clean[..cut].TrimEnd();
        }
        return clean;
    }
}
