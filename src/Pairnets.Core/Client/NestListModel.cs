using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>One nest the app syncs, as the nest list, the "All nests" page and the tray menu show it.</summary>
/// <param name="Id">The app's id for the nest (<see cref="ClientSettings.NestId"/>).</param>
/// <param name="Title">"Home nest".</param>
/// <param name="Folder">The nest's folder on this computer.</param>
/// <param name="Text">"up to date", "syncing 40%", "paused", "can't reach it"...</param>
/// <param name="Sub">The line under the name in the nest list: "Pairnets\Home nest · up to date".</param>
public sealed record NestRow(string Id, string Title, string? Folder, string Text, string Sub, SideDot Dot, SideTone Tone,
    RunnerStatus Icon, int? Percent, bool Selected, string DeviceName, string? AccountEmail)
{
    /// <summary>"Home nest, up to date".</summary>
    public string Tooltip => Title + ", " + Text;
}

/// <summary>
/// The list of nests at the top of the left side when this computer syncs more than one (docs/DECISIONS.md, "Several
/// nests at once"): "All nests", then one row per nest, then "Add a nest". Both apps draw this model.
/// </summary>
/// <param name="AllSub">"2 nests · 1 syncing" under "All nests".</param>
/// <param name="AllSelected">The "All nests" page is showing (no single nest picked).</param>
/// <param name="Worst">The tray icon's colour: the nest in the worst state wins.</param>
public sealed record NestListModel(IReadOnlyList<NestRow> Rows, string AllSub, bool AllSelected, RunnerStatus Worst)
{
    /// <summary>
    /// The line under "Add a nest" on the All nests page. An account may have 4 nests of its own (the service's
    /// MAX_NESTS_PER_ACCOUNT); nests friends share don't count. The app keeps no sign-in to the service, so the website and
    /// the browser step say how many are used.
    /// </summary>
    public const string AddNestHint = "Your own (up to 4 per account), a friend's or one from work. It gets its own folder.";

    /// <summary>The list shows once there are two nests or more; with one, the left side looks as it always did.</summary>
    public bool Visible => Rows.Count > 1;

    public NestRow? Selected => Rows.FirstOrDefault(r => r.Selected);

    /// <summary>The tray icon's hover text: "Pairnets\nHome nest: up to date\nWork nest: syncing 40%".</summary>
    public string Tooltip => "Pairnets" + string.Concat(Rows.Select(r => "\n" + r.Title + ": " + r.Text));

    /// <param name="nests">Each nest's settings and its status (null status: no sync runs for it).</param>
    /// <param name="selectedId">The nest picked in the list, or null for "All nests".</param>
    public static NestListModel Build(ClientSettings all, IEnumerable<(ClientSettings Nest, StatusSnapshot? Status)> nests, string? selectedId,
        string? home = null)
    {
        var rows = new List<NestRow>();
        foreach (var (nest, status) in nests)
        {
            var s = status ?? StatusSnapshot.WithoutSession(nest);
            var (text, dot, tone, percent) = Describe(s);
            var title = NestNames.Title(nest, all, status);
            var place = ShortFolder(nest.Folder, home);
            rows.Add(new NestRow(nest.NestId ?? "", title, nest.Folder, text, place is null ? text : place + " · " + text, dot, tone, s.IconStatus,
                percent, nest.NestId == selectedId, nest.DeviceName ?? "", nest.AccountEmail));
        }
        var syncing = rows.Count(r => r.Icon == RunnerStatus.Syncing);
        var needs = rows.Count(r => r.Icon is RunnerStatus.Error or RunnerStatus.Blocked);
        var allSub = (rows.Count == 1 ? "1 nest" : $"{rows.Count} nests")
            + (needs > 0 ? $" · {needs} need{(needs == 1 ? "s" : "")} you" : syncing > 0 ? $" · {syncing} syncing" : " · all up to date");
        return new NestListModel(rows, allSub, selectedId is null || rows.All(r => !r.Selected), WorstOf(rows.Select(r => r.Icon)));
    }

    /// <summary>The state that wins on the tray icon: an error, then blocked, then can't reach, then paused, then syncing, then up to date.</summary>
    public static RunnerStatus WorstOf(IEnumerable<RunnerStatus> states)
    {
        var list = states.ToList();
        foreach (var s in new[] { RunnerStatus.Error, RunnerStatus.Blocked, RunnerStatus.Offline, RunnerStatus.Paused, RunnerStatus.Syncing })
        {
            if (list.Contains(s))
                return s;
        }
        return list.Count == 0 ? RunnerStatus.Offline : RunnerStatus.Idle;
    }

    /// <summary>A nest's state in a few words, with its dot and colour.</summary>
    public static (string Text, SideDot Dot, SideTone Tone, int? Percent) Describe(StatusSnapshot s)
    {
        int? percent = s.PassBytesTotal > 0 ? (int)Math.Clamp(s.PassBytesDone * 100 / s.PassBytesTotal, 0, 100) : s.Percent;
        if (s.BlockReason == BlockReason.SignedOutByYou)
            return ("signed out", SideDot.Off, SideTone.Neutral, null);
        return s.Status switch
        {
            RunnerStatus.Idle => ("up to date", SideDot.Ok, SideTone.Neutral, null),
            RunnerStatus.Syncing => (percent is { } p ? $"syncing {p}%" : "syncing", SideDot.Ok, SideTone.Accent, percent),
            RunnerStatus.Paused => ("paused", SideDot.Warn, SideTone.Warn, null),
            RunnerStatus.Blocked => ("needs you", SideDot.Warn, SideTone.Warn, null),
            RunnerStatus.Error => ("problem", SideDot.Bad, SideTone.Bad, null),
            _ => ("can't reach it", SideDot.Off, SideTone.Neutral, null),
        };
    }

    /// <summary>"Pairnets\Home nest" for a folder in the home folder, "D:\Work" otherwise.</summary>
    public static string? ShortFolder(string? folder, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.TrimEndingDirectorySeparator(folder);
        if (home.Length > 0)
        {
            var prefix = Path.TrimEndingDirectorySeparator(home) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return full[prefix.Length..];
        }
        return full;
    }
}
