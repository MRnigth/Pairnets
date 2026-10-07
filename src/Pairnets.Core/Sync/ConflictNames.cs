using System.Globalization;
using System.Text;
using Pairnets.Core.Paths;

namespace Pairnets.Core.Sync;

/// <summary>Builds "name (conflict DEVICE yyyy-MM-dd HHmmss).ext" names for conflict copies.</summary>
public static class ConflictNames
{
    public static string Make(string syncPath, string deviceName, DateTime localTime, Func<string, bool> isTaken)
    {
        var parent = PathRules.Parent(syncPath);
        var fileName = PathRules.FileName(syncPath);
        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        if (stem.Length == 0)
        {
            // ".gitignore" style names: keep the whole name as the stem.
            stem = fileName;
            ext = string.Empty;
        }

        var device = SanitizeDevice(deviceName);
        var stamp = localTime.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        for (var counter = 1; counter < 10_000; counter++)
        {
            var suffix = counter == 1 ? $" (conflict {device} {stamp})" : $" (conflict {device} {stamp} {counter})";
            var candidate = Compose(parent, stem, suffix, ext);
            if (candidate is not null && !isTaken(candidate))
                return candidate;
        }
        // Practically unreachable; still never return an existing name.
        string fallback;
        do
        {
            fallback = Compose(parent, "conflict", "-" + Guid.NewGuid().ToString("N")[..12], ext) ?? Guid.NewGuid().ToString("N");
        }
        while (isTaken(fallback));
        return fallback;
    }

    private static string? Compose(string? parent, string stem, string suffix, string ext)
    {
        var prefix = parent is null ? string.Empty : parent + "/";
        var budget = PathRules.MaxPathLength - prefix.Length - suffix.Length - ext.Length;
        if (budget < 1)
            return null;
        if (stem.Length > budget)
            stem = stem[..budget];
        if (char.IsHighSurrogate(stem[^1]))
            stem = stem[..^1];
        stem = stem.TrimEnd(' ', '.');
        if (stem.Length == 0)
            stem = "file";
        var candidate = prefix + stem + suffix + ext;
        return PathRules.IsValid(candidate) ? candidate : null;
    }

    /// <summary>Makes a device name safe to embed in a file name.</summary>
    public static string SanitizeDevice(string? deviceName)
    {
        var sb = new StringBuilder();
        foreach (var c in deviceName ?? string.Empty)
        {
            if (c < 0x20 || c == 0x7F || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\' or '(' or ')')
                sb.Append('_');
            else
                sb.Append(c);
        }
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 32)
            s = s[..32].TrimEnd(' ', '.');
        return s.Length == 0 ? "device" : s;
    }
}
