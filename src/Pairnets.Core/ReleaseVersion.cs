using System.Globalization;

namespace Pairnets.Core;

/// <summary>
/// A Pairnets release number: 2 to 5 numbers, "1.0.61", "1.0.61.8" or "1.0.61.8.1". Compared number by number, a missing
/// number counting as 0 (1.0.61.8 &lt; 1.0.61.8.1 &lt; 1.0.61.9). System.Version stops at four numbers, so dotnet, Windows and
/// macOS get <see cref="FileVersion"/>, the first four; the full number is in the build's informational version, the
/// tag and version.json's "release".
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    public const int MaxParts = 5;

    private readonly int[] _parts;

    private ReleaseVersion(int[] parts) => _parts = parts;

    /// <summary>
    /// "1.0.61.8.1" (a leading "v" and spaces are ignored), or false for anything else: fewer than 2 or more than 5
    /// numbers, signs, letters, numbers above <see cref="int.MaxValue"/>.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = new ReleaseVersion([0, 0]);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var pieces = text.Trim().TrimStart('v').Split('.');
        if (pieces.Length is < 2 or > MaxParts)
            return false;
        var parts = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
                return false;
        }
        version = new ReleaseVersion(parts);
        return true;
    }

    /// <summary>The first four numbers, padded with zeros: what dotnet, Windows and macOS accept ("1.0.61.8.1" → 1.0.61.8.0).</summary>
    public Version FileVersion => new(Part(0), Part(1), Part(2), Part(3));

    private int Part(int i) => i < _parts.Length ? _parts[i] : 0;

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < MaxParts; i++)
        {
            var c = Part(i).CompareTo(other.Part(i));
            if (c != 0)
                return c;
        }
        return 0;
    }

    public override string ToString() => string.Join('.', _parts);
}
