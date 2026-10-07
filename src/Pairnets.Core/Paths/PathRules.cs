namespace Pairnets.Core.Paths;

/// <summary>Why a relative sync path was rejected.</summary>
public enum PathProblem
{
    None = 0,
    Empty,
    TooLong,
    Rooted,
    DriveLetter,
    Backslash,
    EmptySegment,
    DotSegment,
    ControlCharacter,
    InvalidCharacter,
    LeadingOrTrailingSpace,
    TrailingDot,
    ReservedName,
}

/// <summary>
/// Rules for relative sync paths ("folder/sub/file.txt", always '/' separated).
/// The server enforces these so that nothing created on Linux can ever break a
/// Windows client; the client applies the same rules before uploading.
/// </summary>
public static class PathRules
{
    public const int MaxPathLength = 1024;

    /// <summary>Name of the hidden download staging folder inside the sync folder.</summary>
    public const string TempFolderName = ".pairnets-tmp";

    /// <summary>Name of the marker file in the root of the sync folder.</summary>
    public const string MarkerFileName = ".pairnets-marker";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    public static bool IsValid(string? path) => Check(path) == PathProblem.None;

    /// <summary>Checks a relative sync path; returns <see cref="PathProblem.None"/> when it is acceptable.</summary>
    public static PathProblem Check(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return PathProblem.Empty;
        if (path.Length > MaxPathLength)
            return PathProblem.TooLong;
        if (path.Contains('\0'))
            return PathProblem.ControlCharacter;
        if (path.Contains('\\'))
            return PathProblem.Backslash;
        if (path[0] == '/')
            return PathProblem.Rooted;
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            return PathProblem.DriveLetter;

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var problem = CheckSegment(segments[i], isRoot: i == 0, isLast: i == segments.Length - 1);
            if (problem != PathProblem.None)
                return problem;
        }
        return PathProblem.None;
    }

    private static PathProblem CheckSegment(string segment, bool isRoot, bool isLast)
    {
        if (segment.Length == 0)
            return PathProblem.EmptySegment;
        if (segment is "." or "..")
            return PathProblem.DotSegment;

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c < 0x20 || c == 0x7F)
                return PathProblem.ControlCharacter;
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                return PathProblem.InvalidCharacter;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= segment.Length || !char.IsLowSurrogate(segment[i + 1]))
                    return PathProblem.InvalidCharacter;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return PathProblem.InvalidCharacter;
            }
        }

        if (segment[0] == ' ' || segment[^1] == ' ')
            return PathProblem.LeadingOrTrailingSpace;
        if (segment[^1] == '.')
            return PathProblem.TrailingDot;

        // Windows treats "CON", "con.txt", "CON .tar.gz" etc. as devices.
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        if (ReservedDeviceNames.Contains(stem))
            return PathProblem.ReservedName;

        // Pairnets' own bookkeeping names can never be synced, nor those it had as Tether.
        if (string.Equals(segment, TempFolderName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, Legacy.TetherNames.TempFolderName, StringComparison.OrdinalIgnoreCase))
            return PathProblem.ReservedName;
        if (isRoot && isLast && (string.Equals(segment, MarkerFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, Legacy.TetherNames.MarkerFileName, StringComparison.OrdinalIgnoreCase)))
            return PathProblem.ReservedName;

        return PathProblem.None;
    }

    /// <summary>
    /// Key used to detect paths that would be the same file on Windows or macOS: names differing only
    /// in letter case, or in Unicode normalization (macOS treats "é" and "e + combining accent" as
    /// one name).
    /// </summary>
    public static string CaseKey(string path)
    {
        try
        {
            return path.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return path.ToLowerInvariant(); // invalid Unicode; such paths are rejected by Check anyway
        }
    }

    /// <summary>Converts an OS-relative path ("a\b.txt" on Windows) to a sync path ("a/b.txt").</summary>
    public static string FromOsRelative(string osRelative) =>
        Path.DirectorySeparatorChar == '/' ? osRelative : osRelative.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Converts a sync path to an OS-relative path.</summary>
    public static string ToOsRelative(string syncPath) =>
        Path.DirectorySeparatorChar == '/' ? syncPath : syncPath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Resolves a validated sync path under <paramref name="root"/> and verifies that the
    /// canonical result stays inside it. Returns null when the path escapes the root.
    /// </summary>
    public static string? ResolveUnder(string root, string syncPath)
    {
        if (!IsValid(syncPath))
            return null;
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, ToOsRelative(syncPath)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            return null;
        return full;
    }

    /// <summary>
    /// True when any existing component of <paramref name="syncPath"/> below <paramref name="root"/>
    /// is a symlink, junction or other reparse point. Such paths are never followed.
    /// </summary>
    public static bool HasReparsePoint(string root, string syncPath)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var segment in syncPath.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = new FileInfo(current);
            if (!info.Exists)
            {
                info = new DirectoryInfo(current);
                if (!info.Exists)
                {
                    // A dangling symlink reports Exists == false but still has a link target.
                    if (new FileInfo(current).LinkTarget is not null)
                        return true;
                    return false;
                }
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                return true;
        }
        return false;
    }

    /// <summary>Parent sync path ("a/b" for "a/b/c.txt"), or null for a root-level entry.</summary>
    public static string? Parent(string syncPath)
    {
        var idx = syncPath.LastIndexOf('/');
        return idx < 0 ? null : syncPath[..idx];
    }

    /// <summary>File name part of a sync path.</summary>
    public static string FileName(string syncPath)
    {
        var idx = syncPath.LastIndexOf('/');
        return idx < 0 ? syncPath : syncPath[(idx + 1)..];
    }

    /// <summary>All directory prefixes of a path: "a", "a/b" for "a/b/c.txt".</summary>
    public static IEnumerable<string> DirectoryPrefixes(string syncPath)
    {
        for (var i = 0; i < syncPath.Length; i++)
        {
            if (syncPath[i] == '/')
                yield return syncPath[..i];
        }
    }
}
