using System.Text;
using System.Text.RegularExpressions;

namespace Pairnets.Core.Paths;

/// <summary>
/// Glob-based ignore rules, matched case-insensitively.
/// <list type="bullet">
/// <item>"name" or "*.ext" (no slash): matches any path segment (file or folder name).</item>
/// <item>"dir/" (trailing slash): matches folders with that name anywhere.</item>
/// <item>"a/b/*.txt" (inner slash): matches the full relative path (or one of its parents).</item>
/// </list>
/// "*" never crosses a '/', "**" does, "?" matches one character.
/// </summary>
public sealed class IgnoreList
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        "~$*", "*.tmp", "*.temp", "*.swp", "*.swo", "*~", ".~lock.*#",
        "Thumbs.db", "desktop.ini", ".DS_Store", "*.crdownload", "*.part",
        ".git/", PathRules.TempFolderName + "/", PathRules.MarkerFileName,
        Legacy.TetherNames.TempFolderName + "/", Legacy.TetherNames.MarkerFileName,
    ];

    private readonly List<Regex> _segmentRules = [];
    private readonly List<Regex> _directoryRules = [];
    private readonly List<Regex> _pathRules = [];

    public IgnoreList(IEnumerable<string>? extraPatterns = null)
    {
        var all = new List<string>(Defaults);
        if (extraPatterns is not null)
            all.AddRange(extraPatterns);

        foreach (var raw in all)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#'))
                continue;
            pattern = pattern.Replace('\\', '/');
            if (pattern.EndsWith('/'))
            {
                var name = pattern.TrimEnd('/').TrimStart('/');
                if (name.Length == 0)
                    continue;
                if (name.Contains('/'))
                    _pathRules.Add(Compile(name));
                else
                    _directoryRules.Add(Compile(name));
            }
            else if (pattern.Contains('/'))
            {
                _pathRules.Add(Compile(pattern.TrimStart('/')));
            }
            else
            {
                _segmentRules.Add(Compile(pattern));
            }
            Patterns.Add(raw.Trim());
        }
    }

    public List<string> Patterns { get; } = [];

    /// <summary>True when a file at <paramref name="syncPath"/> must not be synced.</summary>
    public bool IsIgnored(string syncPath) => Matches(syncPath, isDirectory: false);

    /// <summary>True when the folder at <paramref name="syncPath"/> (and all its contents) must be skipped.</summary>
    public bool IsIgnoredDirectory(string syncPath) => Matches(syncPath, isDirectory: true);

    private bool Matches(string syncPath, bool isDirectory)
    {
        var segments = syncPath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var segmentIsDirectory = isDirectory || i < segments.Length - 1;
            foreach (var rule in _segmentRules)
            {
                if (rule.IsMatch(segment))
                    return true;
            }
            if (segmentIsDirectory)
            {
                foreach (var rule in _directoryRules)
                {
                    if (rule.IsMatch(segment))
                        return true;
                }
            }
        }

        if (_pathRules.Count > 0)
        {
            // Match the path itself and every parent folder, so "build/out" ignores "build/out/x".
            var prefix = syncPath;
            while (true)
            {
                foreach (var rule in _pathRules)
                {
                    if (rule.IsMatch(prefix))
                        return true;
                }
                var parent = PathRules.Parent(prefix);
                if (parent is null)
                    break;
                prefix = parent;
            }
        }
        return false;
    }

    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
