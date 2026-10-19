using System.Globalization;
using System.Text.RegularExpressions;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>The colour of a status line on the left side: nothing special, blue (busy), amber (needs you) or red.</summary>
public enum SideTone
{
    /// <summary>Plain grey text, no highlight.</summary>
    Neutral,

    /// <summary>Blue: syncing.</summary>
    Accent,

    /// <summary>Amber: stopped, sign in, folder missing.</summary>
    Warn,

    /// <summary>Red: signed out, can't reach.</summary>
    Bad,
}

/// <summary>The little dot on a computer's icon.</summary>
public enum SideDot
{
    Ok,
    Warn,
    Bad,
    Off,
}

/// <summary>What another computer's cell on the left side says (and how its icon looks).</summary>
public enum OtherKind
{
    /// <summary>Moving files right now: "↓ 4.10 MB/s" in blue, blue ring.</summary>
    Live,

    /// <summary>"in sync", green.</summary>
    Ok,

    /// <summary>"next": it comes after the computer this one waits for.</summary>
    Next,

    /// <summary>"online".</summary>
    Online,

    /// <summary>"can't reach": red dashed ring.</summary>
    CantReach,

    /// <summary>"seen 10:45": grey dashed ring, shown when this computer can't reach the nest itself.</summary>
    LastSeen,
}

/// <summary>This computer's card at the top of the left side.</summary>
/// <param name="Text">"This one · sending 4.90 MB/s".</param>
/// <param name="Detail">The same without "This one · " (for tooltips).</param>
/// <param name="Tone">Colours the text and, unless <see cref="SideTone.Neutral"/>, the card behind it.</param>
/// <param name="BarPercent">0–100 while syncing and the progress is known; the card then shows a bar.</param>
public sealed record SideThisComputer(string Name, string Text, string Detail, SideDot Dot, SideTone Tone, int? BarPercent, DeviceIconKind Icon)
{
    public bool Highlighted => Tone != SideTone.Neutral;

    public bool ShowBar => BarPercent is not null;

    /// <summary>"DESKTOP, this computer, sending 4.90 MB/s".</summary>
    public string Tooltip => $"{Name}, this computer, {Detail}";
}

/// <summary>The nest's row.</summary>
public sealed record SideNest(string Text, SideDot Dot, SideTone Tone)
{
    /// <summary>"Your nest, 412 GB free · v1.0.48".</summary>
    public string Tooltip => "Your nest, " + Text;
}

/// <summary>One of the other computers.</summary>
/// <param name="Text">"↓ 4.10 MB/s", "in sync", "next", "online", "can't reach" or "seen 10:45".</param>
/// <param name="Detail">Words for a tooltip: "online, getting files at 4.10 MB/s".</param>
public sealed record SideOther(string Name, string Text, string Detail, OtherKind Kind, DeviceIconKind Icon)
{
    /// <summary>It can be reached (the icon is drawn full, with a green dot).</summary>
    public bool Reachable => Kind is OtherKind.Live or OtherKind.Ok or OtherKind.Next or OtherKind.Online;

    public string Tooltip => $"{Name}, {Detail}";

    public SideDot Dot => Kind switch
    {
        OtherKind.CantReach => SideDot.Bad,
        OtherKind.LastSeen => SideDot.Off,
        _ => SideDot.Ok,
    };
}

/// <summary>The page tiles at the bottom. <see cref="Note"/> is the small live text in the tile's corner, or empty.</summary>
public sealed record SideTiles(string OverviewNote, string ActivityNote, int AttentionCount, string DevicesNote)
{
    /// <summary>Only a Needs-you tile with something in it turns amber.</summary>
    public bool AttentionAlert => AttentionCount > 0;

    public string AttentionNote => AttentionCount > 0 ? AttentionCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
}

public enum UpdateCardKind
{
    None,

    /// <summary>"Pairnets 1.0.49 is ready" with an Update button.</summary>
    Available,

    /// <summary>"Getting Pairnets 1.0.49" with a bar.</summary>
    Downloading,

    /// <summary>"Pairnets 1.0.49 could not be installed".</summary>
    Failed,
}

/// <summary>The slim app-update card above the page tiles.</summary>
/// <param name="Title">"Pairnets 1.0.49 is ready", "Getting Pairnets 1.0.49" or "Pairnets 1.0.49 could not be installed".</param>
/// <param name="Percent">0–100 while downloading.</param>
public sealed record SideUpdate(UpdateCardKind Kind, string Title, string Detail, string Button, int? Percent)
{
    public static SideUpdate None { get; } = new(UpdateCardKind.None, string.Empty, string.Empty, string.Empty, null);

    public bool Visible => Kind != UpdateCardKind.None;

    public bool ShowBar => Kind == UpdateCardKind.Downloading;

    /// <summary>
    /// The detail is written under the title (not only in its tooltip) when it says why nothing was installed, or why the
    /// person has to download the update by hand: the app never opens the download page without saying why.
    /// </summary>
    public bool ShowDetail => Detail.Length > 0
        && (Kind == UpdateCardKind.Failed || (Kind == UpdateCardKind.Available && Button == UpdateTexts.OpenDownloadPage));

    public string PercentText => Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty;

    /// <summary>
    /// Builds the card from what the controllers already say about an update (the same words as the old card's title, detail
    /// and button; <paramref name="title"/> null = no update), so the actions behind it stay the same.
    /// </summary>
    public static SideUpdate From(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        if (title is null)
            return None;
        var version = Regex.Match(title, @"Pairnets\s+(\S+?)(?:…|\.\.\.)?(?:\s|$)") is { Success: true } m ? m.Groups[1].Value : string.Empty;
        var name = version.Length > 0 ? "Pairnets " + version : "Pairnets";
        if (busy)
        {
            var percent = progress is { } p ? (int)Math.Clamp(Math.Round(p), 0, 100) : (int?)null;
            return new SideUpdate(UpdateCardKind.Downloading, "Getting " + name, detail, button, percent ?? 0);
        }
        if (title.Contains("could not", StringComparison.OrdinalIgnoreCase))
            return new SideUpdate(UpdateCardKind.Failed, title, detail, button, null);
        return new SideUpdate(UpdateCardKind.Available, name + " is ready", detail, button, null);
    }
}

/// <summary>
/// What the open left side and the folded dock draw, built from the status snapshot (no UI types): this computer's card, the
/// nest, the other computers (online first, five at most, then "+N"), each page tile's note, and the app-update card. Both
/// apps draw from this, so they say the same words. The wording follows the design's left side (Sidebar.dc.html).
/// </summary>
public sealed record SidePanelModel(
    SideThisComputer This,
    SideNest Nest,
    IReadOnlyList<SideOther> AllOthers,
    string GroupNote,
    SideTiles Tiles,
    SideUpdate Update,
    string AccountInitial,
    string AccountLabel)
{
    /// <summary>The most other computers the group shows.</summary>
    public const int MaxShown = 5;

    /// <summary>How many the folded dock shows before "+N".</summary>
    public const int DockShown = 2;

    /// <summary>Computers not seen for this long are left out (renamed or retired ones); the same as the overview's picture.</summary>
    public static readonly TimeSpan ForgetAfter = DeviceMap.ForgetAfter;

    /// <summary>The other computers the group shows: at most <see cref="MaxShown"/>, online first.</summary>
    public IReadOnlyList<SideOther> Others => AllOthers.Take(MaxShown).ToList();

    /// <summary>How many more there are than the group shows ("+3"); 0 when all fit.</summary>
    public int MoreCount => Math.Max(0, AllOthers.Count - MaxShown);

    public string MoreText => "+" + MoreCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>The group shows "+N" (opening Devices) in its last cell instead of "Add".</summary>
    public bool ShowsMore => MoreCount > 0;

    /// <summary>The other computers the folded dock shows beside this one and the nest.</summary>
    public IReadOnlyList<SideOther> DockOthers => AllOthers.Take(DockShown).ToList();

    /// <summary>The ones the dock folds into "+N" (its tooltip lists them).</summary>
    public IReadOnlyList<SideOther> DockMore => AllOthers.Skip(DockShown).ToList();

    public int DockMoreCount => DockMore.Count;

    public string DockMoreText => "+" + DockMoreCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>"3 more computers" for the dock's "+N" button.</summary>
    public string DockMoreLabel => DockMoreCount == 1 ? "1 more computer" : $"{DockMoreCount} more computers";

    /// <summary>The progress ring around this computer in the dock (0–100), or null when nothing is syncing.</summary>
    public int? RingPercent => This.BarPercent;

    public static SidePanelModel Build(StatusSnapshot s, string? thisDevice, int activityCount, int attentionCount, SideUpdate? update, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(thisDevice) ? "This computer" : thisDevice.Trim();
        var state = StateOf(s);
        var others = OthersOf(s, thisDevice, state, now);
        var online = others.Count(o => o.Reachable);
        var groupNote = state switch
        {
            Side.Offline => "can’t check now",
            Side.SignedOutByYou => "signed out",
            _ => $"{online} of {others.Count} online",
        };
        var devicesNote = s.Devices is null || s.DevicesUnsupported ? string.Empty
            : state == Side.Offline ? "offline"
            : $"{online + 1} on";
        var tiles = new SideTiles(OverviewNote(s, state), activityCount.ToString(CultureInfo.InvariantCulture), Math.Max(0, attentionCount), devicesNote);
        var initial = (s.AccountEmail ?? name).Trim() is { Length: > 0 } source ? char.ToUpperInvariant(source[0]).ToString() : "A";
        var label = s.AccountEmail is { Length: > 0 } email ? "Account: " + email : "Account";
        return new SidePanelModel(ThisOf(s, name, state, now), NestOf(s, state), others, groupNote, tiles, update ?? SideUpdate.None, initial, label);
    }

    // ---- the states the design draws (Sidebar.dc.html: syncing, synced, waiting, stopped, signin, signedout, offline, paused, missing)

    private enum Side
    {
        Syncing,
        Synced,
        Waiting,
        Stopped,
        SignIn,
        SignedOut,
        SignedOutByYou,
        Offline,
        Paused,
        Missing,
        Problem,
    }

    private static Side StateOf(StatusSnapshot s)
    {
        if (s.Status == RunnerStatus.Paused)
            return Side.Paused;
        if (s.Status == RunnerStatus.Offline)
            return Side.Offline;
        if (s.Status == RunnerStatus.Blocked)
        {
            return s.BlockReason switch
            {
                BlockReason.SignedOut => Side.SignedOut,
                BlockReason.SignedOutByYou => Side.SignedOutByYou,
                BlockReason.SignInRequired => Side.SignIn,
                BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => Side.Missing,
                _ => Side.Stopped,
            };
        }
        if (s.Status == RunnerStatus.Error)
            return Side.Problem;
        if (s.IsWaiting)
            return Side.Waiting;
        return s.Status == RunnerStatus.Syncing ? Side.Syncing : Side.Synced;
    }

    private static string OverviewNote(StatusSnapshot s, Side state) => state switch
    {
        Side.Syncing => s.OverallPercent is { } p && s.IsTransferring ? p.ToString(CultureInfo.InvariantCulture) + "%" : "Sync",
        Side.Synced => "Done",
        Side.Waiting => "Wait",
        Side.Stopped or Side.Missing or Side.Problem => "Stop",
        Side.SignIn => "Sign in",
        Side.SignedOut or Side.SignedOutByYou or Side.Offline => "Off",
        _ => "Paused",
    };

    private static SideThisComputer ThisOf(StatusSnapshot s, string name, Side state, DateTimeOffset now)
    {
        var icon = DeviceIcon.ForName(s, name);
        switch (state)
        {
            case Side.Syncing:
            {
                int? bar = s.OverallPercent is { } p ? p : null;
                return new SideThisComputer(name, "This one · " + Doing(s, now), Doing(s, now), SideDot.Ok, SideTone.Accent, s.IsTransferring ? bar ?? 0 : null, icon);
            }
            case Side.Synced:
                return Make("up to date", SideDot.Ok, SideTone.Neutral);
            case Side.Waiting:
                return Make($"waits for {s.WaitingFor!.Device}", SideDot.Ok, SideTone.Neutral);
            case Side.Stopped:
                return Make("stopped to be safe", SideDot.Warn, SideTone.Warn);
            case Side.SignIn:
                return Make("needs to sign in", SideDot.Warn, SideTone.Warn);
            case Side.SignedOut:
                return Make("signed out", SideDot.Bad, SideTone.Bad);
            case Side.SignedOutByYou:
                return Make("signed out", SideDot.Off, SideTone.Neutral);
            case Side.Offline:
                return Make("offline", SideDot.Off, SideTone.Neutral);
            case Side.Paused:
                return Make("paused", SideDot.Off, SideTone.Neutral);
            case Side.Missing:
                return Make("folder is missing", SideDot.Warn, SideTone.Warn);
            default:
                return Make("has a problem", SideDot.Warn, SideTone.Warn);
        }

        SideThisComputer Make(string detail, SideDot dot, SideTone tone) => new(name, "This one · " + detail, detail, dot, tone, null, icon);
    }

    /// <summary>What this computer does while syncing: "sending 4.90 MB/s", "getting 2.00 MB/s", "checking files".</summary>
    private static string Doing(StatusSnapshot s, DateTimeOffset now)
    {
        if (s.IsSlowedDown)
            return "waiting a moment";
        if (s.Motion == SyncMotion.Stalled)
            return "stalled";
        if (s.IsChecking)
            return "checking files";
        var (up, down) = (s.UpBytesPerSecond, s.DownBytesPerSecond);
        if (up < 1 && down < 1 && s.BytesPerSecond >= 1)
            (up, down) = s.GoingDown && !s.GoingUp ? (0d, s.BytesPerSecond) : (s.BytesPerSecond, 0d);
        return (up >= 1, down >= 1) switch
        {
            (true, true) => "syncing " + Format.Speed(up + down),
            (true, false) => "sending " + Format.Speed(up),
            (false, true) => "getting " + Format.Speed(down),
            _ => "syncing",
        };
    }

    private static SideNest NestOf(StatusSnapshot s, Side state)
    {
        switch (state)
        {
            case Side.SignIn:
                return new SideNest("Waiting for you to sign in", SideDot.Warn, SideTone.Warn);
            case Side.SignedOut:
                return new SideNest("This computer was removed", SideDot.Bad, SideTone.Bad);
            case Side.SignedOutByYou:
                return new SideNest("Signed out", SideDot.Off, SideTone.Neutral);
            case Side.Offline:
                return new SideNest("Can’t reach it · trying again", SideDot.Bad, SideTone.Bad);
        }
        if (s.Server is null)
            return new SideNest("Connecting…", SideDot.Off, SideTone.Neutral);
        var parts = new List<string>();
        if (s.Server.DiskFreeBytes is { } free)
            parts.Add(Format.Bytes(free) + " free");
        if (s.Server.ServerVersion is { Length: > 0 } version)
            parts.Add("v" + version);
        return new SideNest(parts.Count > 0 ? string.Join(" · ", parts) : "Online", SideDot.Ok, SideTone.Neutral);
    }

    private static List<SideOther> OthersOf(StatusSnapshot s, string? thisDevice, Side state, DateTimeOffset now)
    {
        if (s.Devices is null || s.DevicesUnsupported)
            return [];
        var cantCheck = state == Side.Offline;
        var waited = s.IsWaiting ? s.WaitingFor?.Device : null;
        var rows = s.Devices
            .Where(d => !string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase) && (d.Online || now - d.LastSeen <= ForgetAfter))
            .Select(d =>
            {
                var live = cantCheck ? null : s.LiveOf(d.Name, thisDevice, now);
                var heard = s.HeardFrom.TryGetValue(d.Name, out var at) && now - at <= TimeSpan.FromMinutes(1);
                return (Device: d, Live: live, Online: !cantCheck && (d.Online || live is not null || heard));
            })
            .OrderByDescending(x => x.Live is not null)
            .ThenByDescending(x => x.Online)
            .ThenByDescending(x => x.Device.LastSeen)
            .ThenBy(x => x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return rows.Select(x => OtherOf(s, x.Device, x.Live, x.Online, cantCheck, waited, state)).ToList();
    }

    private static SideOther OtherOf(StatusSnapshot s, DeviceInfo d, TransferReport? live, bool online, bool cantCheck, string? waited, Side state)
    {
        var icon = DeviceIcon.For(d, s.DeviceIconsSupported);
        if (cantCheck)
        {
            var seen = "seen " + d.LastSeen.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            return new SideOther(d.Name, seen, "last " + seen, OtherKind.LastSeen, icon);
        }
        if (!online)
            return new SideOther(d.Name, "can’t reach", "can’t be reached", OtherKind.CantReach, icon);
        if (live is not null)
        {
            // Its own report: ↓ while it gets files, ↑ while it sends them (the bigger one when it does both).
            var (up, down) = (live.UpBytesPerSecond, live.DownBytesPerSecond);
            if (up >= 1 || down >= 1)
            {
                var getting = down >= up;
                var speed = Format.Speed(getting ? down : up);
                return new SideOther(d.Name, (getting ? "↓ " : "↑ ") + speed, $"online, {(getting ? "getting" : "sending")} files at {speed}", OtherKind.Live, icon);
            }
            return new SideOther(d.Name, "syncing", "online, syncing", OtherKind.Live, icon);
        }
        if (waited is not null && !string.Equals(d.Name, waited, StringComparison.OrdinalIgnoreCase))
            return new SideOther(d.Name, "next", "online, next in line", OtherKind.Next, icon);
        if (state == Side.Synced)
            return new SideOther(d.Name, "in sync", "online, in sync", OtherKind.Ok, icon);
        return new SideOther(d.Name, "online", "online", OtherKind.Online, icon);
    }
}
