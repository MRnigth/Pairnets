using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>The colour family of the tray panel's status circle.</summary>
public enum TrayTone
{
    /// <summary>Blue: syncing or checking.</summary>
    Syncing,
    /// <summary>Green: everything is up to date.</summary>
    Ok,
    /// <summary>Amber, with a pause sign: syncing is paused.</summary>
    Paused,
    /// <summary>Amber, with a warning: it needs a decision (stopped, sign in, folder missing).</summary>
    Warn,
    /// <summary>Red: offline or signed out.</summary>
    Bad,
}

/// <summary>The colour of the words at the right end of a branch row.</summary>
public enum TrayInk
{
    Muted,
    Accent,
    Ok,
    Bad,
}

/// <summary>
/// One small branch off the nest in the tray panel: a computer, the line to it, and what it says.
/// <see cref="Pill"/> is the speed on an active line ("↑ 4.90 MB/s"); <see cref="Word"/> is the word sitting on a quiet line
/// ("in sync", "paused"), used for this computer only; <see cref="Side"/> is the text at the right end.
/// </summary>
public sealed record TrayBranch(string Name, DeviceIconKind Icon, NestLineKind Kind, string? Pill, string? Word, NestDot Dot, string Side, TrayInk SideInk, bool IsThis)
{
    public bool HasPill => Kind == NestLineKind.Active && !string.IsNullOrEmpty(Pill);

    public bool HasWord => !HasPill && !string.IsNullOrEmpty(Word);

    /// <summary>The line is drawn dashed (next in line, paused, stopped, can't reach).</summary>
    public bool Dashed => Kind is NestLineKind.Next or NestLineKind.Warn or NestLineKind.Bad or NestLineKind.Unreachable;

    public bool HasCross => Kind == NestLineKind.Unreachable;
}

/// <summary>The one red row for every computer that can't be reached ("3 can’t be reached").</summary>
public sealed record TrayUnreachable(int Count, IReadOnlyList<DeviceIconKind> Icons, IReadOnlyList<string> Names, string Text)
{
    /// <summary>"MEDIA-PC, OFFICE-PC and KITCHEN-PC can't be reached", for tooltips and screen readers.</summary>
    public string Tooltip => Names.Count switch
    {
        0 => string.Empty,
        1 => Names[0] + " can’t be reached",
        _ => string.Join(", ", Names.Take(Names.Count - 1)) + " and " + Names[^1] + " can’t be reached",
    };
}

/// <summary>How a row of "Latest changes" is drawn.</summary>
public enum TrayChangeKind
{
    /// <summary>Being moved right now (blue square, with a percent).</summary>
    Live,
    Up,
    Down,
    Warn,
    Bad,
    Muted,
}

/// <summary>One row of "Latest changes": a file, what happened, and when (or how far along).</summary>
public sealed record TrayChange(string Name, string Sub, string Right, TrayChangeKind Kind)
{
    public bool Live => Kind == TrayChangeKind.Live;
}

/// <summary>
/// What the tray panel shows, as plain values (no UI types): the status circle and its words, the progress, the nest with a
/// small branch per online computer and one red row for the ones that can't be reached, the buttons, and the latest changes.
/// Both apps draw this; the wording follows the "Tray panel" design.
/// </summary>
public sealed record TrayPanelModel(
    NestMapState State,
    TrayTone Tone,
    string Headline,
    string Detail,
    int? Percent,
    string? FilesText,
    string? LeftText,
    string? PausedNote,
    string NestSub,
    NestDot NestDot,
    TrayBranch This,
    IReadOnlyList<TrayBranch> Others,
    TrayUnreachable? Unreachable,
    string? MoreText,
    string? FixLabel,
    bool Paused,
    string NestTitle = "Your nest")
{
    /// <summary>The computers in the small pile on the red row.</summary>
    public const int MaxUnreachableIcons = 3;

    public bool ShowsProgress => Percent is not null;

    public string PauseLabel => Paused ? "Resume" : "Pause";

    /// <summary>When paused, Resume is the main button and Open Pairnets steps back.</summary>
    public bool ResumeIsPrimary => Paused;

    /// <summary>Every branch row, top to bottom: this computer, then the others that can be reached.</summary>
    public IEnumerable<TrayBranch> Branches => Others.Prepend(This);

    /// <summary>Changes whenever the branches would look different (so the UI redraws them only then).</summary>
    public string BranchKey => string.Join("|", Branches.Select(b => b.ToString()))
        + (Unreachable is { } u ? $"|{u.Count}:{string.Join(",", u.Icons)}" : string.Empty) + "|" + MoreText;

    /// <summary>"3 things need you" for the amber row (null when nothing does).</summary>
    public static string? AttentionText(int count) => count switch
    {
        <= 0 => null,
        1 => "1 thing needs you",
        _ => count.ToString(CultureInfo.InvariantCulture) + " things need you",
    };

    /// <summary>How many "Latest changes" rows fit under the rest.</summary>
    public int ChangesLimit(bool attention) => Math.Max(2, 4 - (ShowsProgress ? 1 : 0) - (attention ? 1 : 0) - (FixLabel is not null ? 1 : 0));

    public static TrayPanelModel Build(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        var map = NestMapModel.Build(s, thisDevice, now);
        var state = map.State;
        var paused = state == NestMapState.Paused;

        var tone = state switch
        {
            NestMapState.Syncing or NestMapState.Waiting => TrayTone.Syncing,
            NestMapState.UpToDate => TrayTone.Ok,
            NestMapState.Paused or NestMapState.SignedOutByYou => TrayTone.Paused,
            NestMapState.SignedOut or NestMapState.Offline => TrayTone.Bad,
            _ => TrayTone.Warn,
        };

        string headline;
        string detail;
        int? percent = null;
        string? files = null;
        string? left = null;
        string? note = null;
        switch (state)
        {
            case NestMapState.UpToDate:
                headline = "Everything is up to date";
                detail = SyncedText(s.LastSyncAt, now);
                break;
            case NestMapState.Paused:
                headline = "Syncing is paused";
                detail = SyncedText(s.LastSyncAt, now);
                note = "New changes wait here until you resume.";
                break;
            case NestMapState.SignedOutByYou:
                headline = "Signed out";
                detail = "Sign in to keep this folder in sync.";
                break;
            case NestMapState.Syncing when s.IsTransferring:
                headline = "Syncing…";
                detail = s.BatchTitle + Direction(s);
                percent = s.OverallPercent ?? s.Percent ?? 0;
                files = s.OverallText.Length > 0 ? s.OverallText : null;
                left = TimeLeft(s);
                break;
            default:
                headline = s.Headline;
                detail = s.IsWaiting ? s.WaitingProgressAt(now) : s.DetailAt(now) is { Length: > 0 } d ? d : s.LastSyncText;
                break;
        }

        var thisBranch = new TrayBranch(thisDevice ?? "This computer", map.This.Icon, map.This.Kind,
            map.This.HasPill ? map.This.Label : null, map.This.Label, map.This.Dot, "this computer", TrayInk.Muted, true);

        var branches = new List<TrayBranch>();
        var gone = new List<NestMapOther>();
        foreach (var o in map.Others)
        {
            if (o.Kind == NestLineKind.Unreachable)
            {
                gone.Add(o);
                continue;
            }
            var ink = o.Kind switch
            {
                NestLineKind.Active => TrayInk.Accent,
                NestLineKind.Ok => TrayInk.Ok,
                _ => TrayInk.Muted,
            };
            branches.Add(new TrayBranch(o.Name, o.Icon, o.Kind, o.HasPill ? o.Speed : null, null, o.Dot, o.Status, ink, false));
        }

        TrayUnreachable? unreachable = gone.Count == 0
            ? null
            : new TrayUnreachable(gone.Count, gone.Take(MaxUnreachableIcons).Select(g => g.Icon).ToList(), gone.Select(g => g.Name).ToList(),
                gone.Count.ToString(CultureInfo.InvariantCulture) + " can’t be reached");

        var more = map.MoreCount > 0 ? "+" + map.MoreCount.ToString(CultureInfo.InvariantCulture) + " more" : null;
        return new TrayPanelModel(state, tone, headline, detail, percent, files, left, note, map.NestSub, map.NestDot,
            thisBranch, branches, unreachable, more, s.FixLabel, s.Paused || paused, map.NestTitle);
    }

    /// <summary>
    /// The rows of "Latest changes": what is being moved now first (when something is), then the newest activity, at most
    /// <paramref name="limit"/> in all.
    /// </summary>
    public static IReadOnlyList<TrayChange> Changes(StatusSnapshot s, IReadOnlyList<ActivityItem> items, int limit, DateTimeOffset now)
    {
        var rows = new List<TrayChange>();
        if (s.IsTransferring && s.CurrentFileName is { } file)
        {
            var up = s.GoingUp || !s.GoingDown;
            rows.Add(new TrayChange(file, up ? "Uploading now" : "Downloading now", s.Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty, TrayChangeKind.Live));
        }
        foreach (var item in items)
        {
            if (rows.Count >= limit)
                break;
            rows.Add(new TrayChange(item.Primary, item.Title, WhenText(item.Time, now), item.Kind switch
            {
                ActivityKind.Uploaded => TrayChangeKind.Up,
                ActivityKind.Downloaded => TrayChangeKind.Down,
                ActivityKind.Conflict or ActivityKind.Blocked => TrayChangeKind.Warn,
                ActivityKind.DeletedHere or ActivityKind.DeletedOnServer or ActivityKind.Warning or ActivityKind.Error => TrayChangeKind.Bad,
                _ => TrayChangeKind.Muted,
            }));
        }
        return rows.Take(limit).ToList();
    }

    /// <summary>"Last synced today at 10:45", "yesterday at 23:05", or "Not synced yet".</summary>
    public static string SyncedText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t)
            return "Not synced yet";
        var local = t.ToLocalTime();
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (now.ToLocalTime().Date - local.Date).Days;
        return days switch
        {
            0 => "Last synced today at " + clock,
            1 => "Last synced yesterday at " + clock,
            _ => "Last synced " + local.ToString("d MMM", CultureInfo.InvariantCulture) + " at " + clock,
        };
    }

    /// <summary>"10:52" for today, "5 Oct" for older.</summary>
    private static string WhenText(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>" to your nest" / " from your nest" after "Uploading 120 files" (nothing after a mixed "Syncing 50 files").</summary>
    private static string Direction(StatusSnapshot s) => s.GoingUp && !s.GoingDown ? " to your nest" : s.GoingDown && !s.GoingUp ? " from your nest" : string.Empty;

    /// <summary>"4 min left" from what is left to move and the speed, or null while the speed is not known.</summary>
    private static string? TimeLeft(StatusSnapshot s)
    {
        var left = s.PassBytesTotal - s.PassBytesDone;
        if (s.BytesPerSecond < 1 || left <= 0)
            return null;
        var text = Format.Duration(TimeSpan.FromSeconds(left / s.BytesPerSecond));
        return (text.StartsWith("about ", StringComparison.Ordinal) ? text[6..] : text) + " left";
    }
}
