using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pairnets.Core.Client;

/// <summary>
/// What macOS itself wrote about Pairnets and about the Mac going down, for the bug report: Pairnets' own crash reports
/// (.ips, from the last 14 days) with the kind of crash and the top frames of the thread that crashed, and the names and
/// dates of kernel panic, Jetsam (out of memory) and reset reports, with the start of a panic's text. Looked at only
/// when the person opens "Report a bug"; read here, shown in the preview, sent only on Send.
/// </summary>
public static class MacReports
{
    public static readonly TimeSpan Age = TimeSpan.FromDays(14);
    private const int TopFrames = 15;
    private const int PanicLines = 6;

    /// <summary>The two folders macOS keeps its reports in: this account's, and the whole Mac's.</summary>
    public static IReadOnlyList<string> DefaultFolders() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports"),
        "/Library/Logs/DiagnosticReports",
    ];

    /// <summary>The report text, or "" when this is not a Mac (unless <paramref name="force"/>, for tests).</summary>
    public static string Build(IEnumerable<string>? folders = null, DateTimeOffset? now = null, bool force = false)
    {
        if (!force && !OperatingSystem.IsMacOS())
            return string.Empty;
        var cutoff = (now ?? DateTimeOffset.UtcNow) - Age;
        var inv = CultureInfo.InvariantCulture;
        var own = new StringBuilder();
        var others = new StringBuilder();
        foreach (var folder in folders ?? DefaultFolders())
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(folder))
                    continue;
                files = Directory.EnumerateFiles(folder).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                others.AppendLine(inv, $"(a Mac reports folder could not be read: {ex.GetType().Name})");
                continue;
            }
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                DateTimeOffset when;
                try
                {
                    when = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (when < cutoff)
                    continue;
                if (IsOwn(name))
                {
                    own.AppendLine(inv, $"{name} ({when.ToLocalTime():yyyy-MM-dd HH:mm})");
                    own.AppendLine(Summary(file));
                }
                else if (IsSystemDown(name))
                {
                    others.AppendLine(inv, $"{name} ({when.ToLocalTime():yyyy-MM-dd HH:mm})");
                    if (name.EndsWith(".panic", StringComparison.OrdinalIgnoreCase))
                        others.AppendLine(PanicStart(file));
                }
            }
        }
        if (own.Length == 0 && others.Length == 0)
            return "No Pairnets crash reports and no panic, out-of-memory or reset reports from macOS in the last 14 days.";
        var sb = new StringBuilder();
        if (own.Length > 0)
        {
            sb.AppendLine("macOS crash reports for Pairnets:");
            sb.Append(own);
        }
        if (others.Length > 0)
        {
            sb.AppendLine("Reports about the Mac itself (panic, out of memory, reset):");
            sb.Append(others);
        }
        return sb.ToString().TrimEnd();
    }

    internal static bool IsOwn(string name) =>
        name.StartsWith("Pairnets", StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith(".ips", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".crash", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSystemDown(string name) =>
        name.EndsWith(".panic", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("JetsamEvent", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ResetCounter", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("Reset", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ips", StringComparison.OrdinalIgnoreCase))
        || name.Contains("shutdown_stall", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".spin", StringComparison.OrdinalIgnoreCase)
        || (name.Contains("hang", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ips", StringComparison.OrdinalIgnoreCase));

    /// <summary>The kind of crash and the top frames of the thread that crashed, from a .ips (two JSON parts) or old-style .crash file.</summary>
    internal static string Summary(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            // .ips: the first line is a small JSON header, the rest is the report as JSON.
            var cut = text.IndexOf('\n');
            if (cut > 0 && text.TrimStart().StartsWith('{') && text[(cut + 1)..].TrimStart().StartsWith('{'))
                return FromIps(text[(cut + 1)..]);
            return string.Join('\n', text.Split('\n').Take(40)).TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"  (could not read it: {ex.GetType().Name})";
        }
    }

    private static string FromIps(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("exception", out var exception))
            sb.AppendLine($"  exception: {Text(exception, "type")} {Text(exception, "signal")} {Text(exception, "subtype")}".TrimEnd());
        if (root.TryGetProperty("termination", out var term))
            sb.AppendLine($"  termination: {Text(term, "namespace")} {Text(term, "indicator")} {Text(term, "reasons")}".TrimEnd());
        if (root.TryGetProperty("asi", out var asi))
            sb.AppendLine("  asi: " + Cut(asi.GetRawText(), 400));
        if (root.TryGetProperty("vmSummary", out var vm))
            sb.AppendLine("  memory: " + Cut(vm.ValueKind == JsonValueKind.String ? vm.GetString() ?? string.Empty : vm.GetRawText(), 300));
        if (root.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array)
        {
            var faulting = root.TryGetProperty("faultingThread", out var f) && f.TryGetInt32(out var fi) ? fi : 0;
            var index = 0;
            foreach (var thread in threads.EnumerateArray())
            {
                if (index++ != faulting)
                    continue;
                sb.AppendLine("  crashed thread:");
                if (!thread.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
                    break;
                var images = root.TryGetProperty("usedImages", out var u) && u.ValueKind == JsonValueKind.Array ? u.EnumerateArray().ToList() : [];
                foreach (var frame in frames.EnumerateArray().Take(TopFrames))
                {
                    var image = frame.TryGetProperty("imageIndex", out var ii) && ii.TryGetInt32(out var n) && n >= 0 && n < images.Count
                        ? Text(images[n], "name") : "?";
                    var symbol = frame.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                    var offset = frame.TryGetProperty("imageOffset", out var o) ? o.ToString() : "?";
                    sb.AppendLine($"    {image}  {symbol ?? "+" + offset}");
                }
                break;
            }
        }
        return sb.Length == 0 ? "  (no details in it)" : sb.ToString().TrimEnd();
    }

    private static string PanicStart(string file)
    {
        try
        {
            var lines = File.ReadLines(file).Take(200).ToList();
            var at = lines.FindIndex(l => l.Contains("panicString", StringComparison.OrdinalIgnoreCase) || l.Contains("panic(", StringComparison.OrdinalIgnoreCase));
            var from = at >= 0 ? at : 0;
            return string.Join('\n', lines.Skip(from).Take(PanicLines).Select(l => "  " + Cut(l, 300)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "  (could not read it: only the name is shown)";
        }
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText()) : string.Empty;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + " …";
}
