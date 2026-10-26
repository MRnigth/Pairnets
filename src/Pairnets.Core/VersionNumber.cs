using System.Globalization;

namespace Pairnets.Core;

/// <summary>
/// A Pairnets release number: 2 to 5 numbers, "1.0.61", "1.0.61.8" or "1.0.61.8.1". Compared number by number, a missing
/// number counting as 0 (1.0.61.8 &lt; 1.0.61.8.1 &lt; 1.0.61.9). System.Version stops at four numbers, so dotnet, Windows and
/// macOS get <see cref="FileVersion"/>, the first four; the full number is in the build's informational version, the
/// tag and version.json's "release".
/// </summary>
public sealed class VersionNumber : IComparable<VersionNumber>, IEquatable<VersionNumber>
{
    public const int MaxParts = 5;

    public static readonly VersionNumber Zero = new([0, 0, 0, 0]);

    private readonly int[] _parts;

    private VersionNumber(int[] parts) => _parts = parts;

    /// <summary>
    /// "1.0.61.8.1" (a leading "v" and spaces are ignored), or false for anything else: fewer than 2 or more than 5
    /// numbers, signs, letters, numbers above <see cref="int.MaxValue"/>.
    /// </summary>
    public static bool TryParse(string? text, out VersionNumber version)
    {
        version = Zero;
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
        version = new VersionNumber(parts);
        return true;
    }

    /// <summary>A System.Version's numbers (an unset build or revision counts as 0).</summary>
    public static implicit operator VersionNumber(Version v) =>
        new([v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)]);

    /// <summary>The first four numbers, padded with zeros: what dotnet, Windows and macOS accept ("1.0.61.8.1" → 1.0.61.8.0).</summary>
    public Version FileVersion => new(Part(0), Part(1), Part(2), Part(3));

    /// <summary>The numbers without trailing zeros past the third: "1.0.58", "1.0.59.1", "1.0.61.8.1".</summary>
    public IReadOnlyList<int> Significant
    {
        get
        {
            var n = Math.Max(_parts.Length, 3);
            while (n > 3 && Part(n - 1) == 0)
                n--;
            return Enumerable.Range(0, n).Select(Part).ToArray();
        }
    }

    /// <summary>One lower in its last significant number: 1.0.61.8.2 → 1.0.61.8.1, 1.0.61.8.1 → 1.0.61.8, 1.0.62 → 1.0.61.</summary>
    public VersionNumber Previous()
    {
        var p = Significant.ToArray();
        if (p.Length > 3)
            return p[^1] > 1 ? new VersionNumber([.. p[..^1], p[^1] - 1]) : new VersionNumber(p[..^1]);
        return p[2] > 0 ? new VersionNumber([p[0], p[1], p[2] - 1]) : this;
    }

    private int Part(int i) => i < _parts.Length ? _parts[i] : 0;

    public int CompareTo(VersionNumber? other)
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

    public bool Equals(VersionNumber? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VersionNumber v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Part(0), Part(1), Part(2), Part(3), Part(4));

    public static bool operator ==(VersionNumber? a, VersionNumber? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(VersionNumber? a, VersionNumber? b) => !(a == b);

    public static bool operator >(VersionNumber a, VersionNumber b) => a.CompareTo(b) > 0;

    public static bool operator <(VersionNumber a, VersionNumber b) => a.CompareTo(b) < 0;

    public static bool operator >=(VersionNumber a, VersionNumber b) => a.CompareTo(b) >= 0;

    public static bool operator <=(VersionNumber a, VersionNumber b) => a.CompareTo(b) <= 0;

    /// <summary>All its numbers as written ("1.0.61.8.0" stays so); <see cref="PairnetsInfo.FormatVersion(VersionNumber?)"/> trims.</summary>
    public override string ToString() => string.Join('.', _parts);
}
