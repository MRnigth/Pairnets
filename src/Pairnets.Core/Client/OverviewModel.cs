using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>How a piece of the Overview is coloured.</summary>
public enum OverviewTone
{
    Neutral,
    Accent,
    Ok,
    Warn,
    Bad,
}

/// <summary>The small pictures the Overview uses (both apps draw them from the same line shapes).</summary>
public enum OverviewIcon
{
    None,
    Check,
    Hourglass,
    Shield,
    Key,
    UserX,
    CloudOff,
    Pause,
    Play,
    FolderQuestion,
    Folder,
    FolderFill,
    Sync,
    List,
    External,
    SignIn,
    Search,
    Clock,
    Up,
    Down,
    Computer,
    Server,
    Disk,
    File,
    Image,
    Video,
    Document,
    Info,
    Warning,
}

/// <summary>What a button on the Overview does (the window forwards it to what it already does).</summary>
public enum OverviewAction
{
    None,

    /// <summary>Allow deletions, locate the folder, sign in again: whatever the blocked state needs.</summary>
    Fix,
    SyncNow,
    TogglePause,
    OpenFolder,
    DownloadNow,
    OpenAccount,
    SeeList,
    CheckNestUpdate,
    SeeActivity,
    Review,
}

public enum OverviewButtonKind
{
    Primary,
    Warn,
    Secondary,
    Link,

    /// <summary>An outlined amber button: a choice that deletes or overwrites, never the bright one.</summary>
    WarnOutline,
}

/// <summary>How the header's "Sync now" button looks in a state.</summary>
public enum SyncNowLook
{
    /// <summary>The main (blue) button: syncing now is the useful thing to do.</summary>
    Primary,

    /// <summary>A plain button: the card has its own action (sign in, resume, try now, a stop to decide).</summary>
    Plain,

    /// <summary>A plain button that cannot be pressed: a sync is already running.</summary>
    Busy,
}

/// <summary>The header's "Sync now" button, the same in both apps.</summary>
public sealed record OverviewSyncNow(SyncNowLook Look)
{
    public string Label => "Sync now";

    public bool IsPrimary => Look == SyncNowLook.Primary;

    public bool IsEnabled => Look != SyncNowLook.Busy;

    /// <summary>The tooltip, with the keyboard shortcut (<paramref name="mac"/>: ⌘R, else Ctrl+R).</summary>
    public string ToolTip(bool mac) => Look == SyncNowLook.Busy
        ? "Pairnets is syncing now"
        : Shortcuts.WithHint("Sync now", ShortcutAction.SyncNow, mac);

    /// <summary>
    /// Only up to date (or waiting for another computer) makes "Sync now" the main button. While syncing it rests; signed
    /// out, offline, paused and stopped have their own button on the card, so the header's one turns plain.
    /// </summary>
    public static OverviewSyncNow For(NestMapState state) => new(state switch
    {
        NestMapState.UpToDate or NestMapState.Waiting => SyncNowLook.Primary,
        NestMapState.Syncing => SyncNowLook.Busy,
        _ => SyncNowLook.Plain,
    });
}

/// <summary>Which way the files move, for the picture's motion and the checks.</summary>
public enum OverviewFlow
{
    None,
    Up,
    Down,
    Both,
    Checking,
}

public sealed record OverviewButton(string Label, OverviewIcon Icon, OverviewAction Action, OverviewButtonKind Kind, bool Large = false);

public sealed record OverviewInfo(OverviewIcon Icon, string Text, OverviewTone Tone = OverviewTone.Neutral);

public sealed record OverviewTile(string Label, string Value, string? Sub = null, int? Percent = null);

public sealed record OverviewFileLine(string Name, string Sub, OverviewIcon Icon);

public sealed record OverviewDeleteBox(string Heading, string CountText, IReadOnlyList<OverviewFileLine> Files, string? More);

public sealed record OverviewFolderBox(string Path, string Sub, string Badge);

public sealed record OverviewWaitBox(string Title, string Sub, int Percent, OverviewButton? Skip);

/// <summary>The bar under the heading while syncing: percent (null while unknown), the two lines under it.</summary>
public sealed record OverviewProgress(int? Percent, string Left, string Right, bool RightWarn);

/// <summary>One line of "Latest changes".</summary>
public sealed record OverviewChange(OverviewIcon Icon, OverviewTone Tone, string Name, string Sub, string Time);

/// <summary>One cell of the details card (or the details strip): a label, its value and maybe a link.</summary>
public sealed record OverviewFact(OverviewIcon Icon, string Label, string Value, OverviewTone Tone = OverviewTone.Neutral, string? LinkText = null, OverviewAction LinkAction = OverviewAction.None);

/// <summary>One file inside a folder row of "Moving now".</summary>
public sealed record OverviewMovingFile(string Name, int? Percent, string StateText, OverviewIcon Icon);

/// <summary>A row of "Moving now": a folder (opens to list its files) or one file.</summary>
public sealed record OverviewMovingRow(string Key, string Title, string Sub, int? Percent, bool IsFolder, OverviewIcon Icon, IReadOnlyList<OverviewMovingFile> Files)
{
    public string PercentText => Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty;
}

/// <summary>Which cards fill the lower part of the page.</summary>
public enum OverviewLower
{
    /// <summary>Only the "Moving now" card.</summary>
    Moving,

    /// <summary>"Latest changes" next to a details card.</summary>
    ChangesAndFacts,

    /// <summary>Only a strip of details.</summary>
    Strip,
}

/// <summary>
/// Everything the Overview page shows, as plain values (no UI types): the heading card for each of the nine states, the
/// "needs you" strip, and the cards below. Built from <see cref="StatusSnapshot"/>; both apps draw it. The wording follows
/// the Ov-*.dc.html designs. The picture of the computers is <see cref="NestMapModel"/>.
/// </summary>
public sealed record OverviewModel
{
    public NestMapState State { get; init; }

    /// <summary>How the header's "Sync now" button looks (<see cref="OverviewSyncNow.For"/>).</summary>
    public OverviewSyncNow SyncNow { get; init; } = new(SyncNowLook.Primary);

    /// <summary>The heading card's outline: amber for a stop, red for signed out.</summary>
    public OverviewTone Border { get; init; }

    // ---- the heading

    /// <summary>True: a small "Syncing" chip, the title and a big percent over a bar. False: a round icon, the title and its words.</summary>
    public bool IsSyncingBar { get; init; }

    public string Chip { get; init; } = string.Empty;
    public OverviewTone ChipTone { get; init; }
    public OverviewIcon Glyph { get; init; }
    public OverviewTone GlyphTone { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>The small pill next to the title ("Needs your OK"), or null.</summary>
    public string? Badge { get; init; }

    public OverviewTone BadgeTone { get; init; }
    public string Detail { get; init; } = string.Empty;
    public OverviewProgress? Progress { get; init; }

    /// <summary>A button at the right of the heading ("Try now", "Resume"), or null.</summary>
    public OverviewButton? HeadButton { get; init; }

    // ---- what sits under the heading (each only for its state)

    public OverviewWaitBox? WaitBox { get; init; }
    public OverviewDeleteBox? DeleteBox { get; init; }
    public OverviewFolderBox? FolderBox { get; init; }

    /// <summary>A coloured note ("Signed out today. Didn't do this yourself?").</summary>
    public string? Note { get; init; }

    public IReadOnlyList<OverviewInfo> Checks { get; init; } = [];
    public IReadOnlyList<OverviewInfo> Infos { get; init; } = [];
    public IReadOnlyList<OverviewTile> Tiles { get; init; } = [];
    public IReadOnlyList<OverviewButton> Buttons { get; init; } = [];

    /// <summary>The small grey words next to the buttons.</summary>
    public string? ButtonNote { get; init; }

    // ---- the strip and the cards

    /// <summary>"3 things need you", or null.</summary>
    public string? NeedsTitle { get; init; }

    public string NeedsDetail { get; init; } = string.Empty;
    public OverviewLower Lower { get; init; }
    public IReadOnlyList<OverviewMovingRow> Moving { get; init; } = [];

    /// <summary>"4 of 120 files".</summary>
    public string MovingCount { get; init; } = string.Empty;

    public IReadOnlyList<OverviewChange> Changes { get; init; } = [];
    public IReadOnlyList<OverviewFact> Facts { get; init; } = [];

    /// <summary>Which way files move now (the picture's dots, and the checks).</summary>
    public OverviewFlow Flow { get; init; }

    /// <summary>True while a button of the heading would act on a blocked pass (the checks).</summary>
    public bool HasFix => Buttons.Any(b => b.Action == OverviewAction.Fix) || HeadButton?.Action == OverviewAction.Fix;

    /// <summary>True while the heading's round icon should breathe (waiting for another computer).</summary>
    public bool Pulses => State == NestMapState.Waiting;

    public bool ShowsMoving => Lower == OverviewLower.Moving;

    /// <summary>The words of the nest's host for "Opens nest.example.com in your browser".</summary>
    public static string HostOf(StatusSnapshot s)
    {
        if (s.NestUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return "your nest";
    }

    public static OverviewModel Build(StatusSnapshot s, string? thisDevice, string? folder, int attentionCount, IReadOnlyList<ActivityItem> recent, DateTimeOffset now)
    {
        var state = NestMapModel.StateOf(s);
        var host = HostOf(s);
        var hostText = host == "your nest" ? "your nest" : host;
        var needsTitle = attentionCount <= 0 ? null : attentionCount == 1 ? "1 thing needs you" : $"{attentionCount.ToString(CultureInfo.InvariantCulture)} things need you";
        var needsDetail = NeedsText(s, attentionCount);
        var folderText = string.IsNullOrWhiteSpace(folder) ? "Not chosen yet" : folder;
        var lastSynced = s.LastSyncAt is { } at ? At(Format.Moment(at, now)) : null;
        var flow = FlowOf(s);

        var model = new OverviewModel { State = state, SyncNow = OverviewSyncNow.For(state), NeedsTitle = needsTitle, NeedsDetail = needsDetail, Flow = flow };
        var open = new OverviewFact(OverviewIcon.FolderFill, "Your folder", folderText, LinkText: "Open", LinkAction: OverviewAction.OpenFolder);
        var version = VersionFact(s);
        var space = s.Server?.DiskFreeBytes is { } free
            ? new OverviewFact(OverviewIcon.Disk, "Free space", Format.Bytes(free) + " on the nest", s.ServerSpaceLow ? OverviewTone.Warn : OverviewTone.Neutral)
            : null;
        var changes = ChangesOf(recent, now);

        switch (state)
        {
            case NestMapState.Syncing:
            {
                return model with
                {
                    IsSyncingBar = true,
                    Chip = "Syncing",
                    ChipTone = s.Motion == SyncMotion.Stalled || s.IsSlowedDown ? OverviewTone.Warn : OverviewTone.Accent,
                    Title = SyncingTitle(s),
                    Progress = SyncingProgress(s, now),
                    Lower = OverviewLower.Moving,
                    Moving = MovingRows(s),
                    MovingCount = MovingCountText(s),
                };
            }

            case NestMapState.UpToDate:
                return model with
                {
                    Glyph = OverviewIcon.Check,
                    GlyphTone = OverviewTone.Ok,
                    Title = "Everything is up to date",
                    Detail = (lastSynced is null ? "Connected to your nest" : $"Last synced {lastSynced} · connected to your nest"),
                    Lower = OverviewLower.ChangesAndFacts,
                    Changes = changes,
                    Facts = FactsOf(open, version, space),
                };

            case NestMapState.Waiting:
            {
                var who = s.WaitingFor?.Device ?? "another computer";
                var counts = s.WaitingCounts(now);
                var total = counts?.Total ?? 0;
                var done = counts?.Done ?? 0;
                var live = s.WaitingFor is { } w ? s.LiveOf(w.Device, null, now) : null;
                var flowText = live is null ? string.Empty : Format.Flow(live.UpBytesPerSecond, live.DownBytesPerSecond);
                var sub = total > 0 ? $"{Format.Count(done)} of {Format.Count(total)} files" : "Starting";
                if (flowText.Length > 0)
                    sub += " · " + flowText;
                return model with
                {
                    Glyph = OverviewIcon.Hourglass,
                    GlyphTone = OverviewTone.Accent,
                    Title = "Waiting for " + who,
                    Detail = $"{who} is sending a big batch to your nest. You get it all in one go when it’s done.",
                    WaitBox = new OverviewWaitBox(total > 0 ? $"{who} is sending {Format.Count(total)} files" : $"{who} is sending a big batch", sub,
                        s.WaitingPercent ?? 0, new OverviewButton("Download now anyway", OverviewIcon.None, OverviewAction.DownloadNow, OverviewButtonKind.Link)),
                    Lower = OverviewLower.ChangesAndFacts,
                    Changes = changes,
                    Facts = FactsOf(open, lastSynced is null ? null : new OverviewFact(OverviewIcon.Clock, "Last synced", Capital(lastSynced))),
                };
            }

            case NestMapState.Stopped:
                return Stopped(model, s, open, lastSynced);

            case NestMapState.SignIn:
                return model with
                {
                    Glyph = OverviewIcon.Key,
                    GlyphTone = OverviewTone.Accent,
                    Title = "Sign in to your nest",
                    Detail = "Your nest now has a private sign-in. Sign in once and syncing picks up again.",
                    Checks =
                    [
                        new OverviewInfo(OverviewIcon.Check, "Your files stay as they are", OverviewTone.Ok),
                        new OverviewInfo(OverviewIcon.Check, "Takes about a minute", OverviewTone.Ok),
                        new OverviewInfo(OverviewIcon.Check, "You only do this once", OverviewTone.Ok),
                    ],
                    Buttons = [new OverviewButton("Sign in with your browser", OverviewIcon.External, OverviewAction.Fix, OverviewButtonKind.Primary, Large: true)],
                    ButtonNote = $"Opens {hostText} in your browser",
                    Lower = OverviewLower.ChangesAndFacts,
                    Changes = changes.Take(2).ToList(),
                    Facts = FactsOf(open, new OverviewFact(OverviewIcon.Key, "Account", "Not signed in yet")),
                };

            case NestMapState.SignedOut:
                return model with
                {
                    Border = OverviewTone.Bad,
                    Glyph = OverviewIcon.UserX,
                    GlyphTone = OverviewTone.Bad,
                    Title = "Signed out of your nest",
                    Badge = "Not syncing",
                    BadgeTone = OverviewTone.Bad,
                    Detail = "This computer was removed from your nest, or its key was turned off. It stays stopped until you sign in again. Your files here stay as they are.",
                    Note = "Didn’t do this yourself? After you sign in, check which computers can use your nest.",
                    Buttons =
                    [
                        new OverviewButton("Sign in again", OverviewIcon.SignIn, OverviewAction.Fix, OverviewButtonKind.Primary),
                        new OverviewButton("Open your account page", OverviewIcon.External, OverviewAction.OpenAccount, OverviewButtonKind.Secondary),
                    ],
                    Lower = OverviewLower.Strip,
                    Facts =
                    [
                        open,
                        new OverviewFact(OverviewIcon.Server, "Your nest", hostText),
                        new OverviewFact(OverviewIcon.Key, "Account", "Signed out", OverviewTone.Bad),
                    ],
                };

            case NestMapState.SignedOutByYou:
                return model with
                {
                    Glyph = OverviewIcon.UserX,
                    GlyphTone = OverviewTone.Neutral,
                    Title = "You’re signed out",
                    Detail = "Sign in to keep this folder in sync. Your files here stay as they are.",
                    Buttons = [new OverviewButton("Sign in…", OverviewIcon.SignIn, OverviewAction.Fix, OverviewButtonKind.Primary)],
                    Lower = OverviewLower.Strip,
                    Facts = [open, new OverviewFact(OverviewIcon.Key, "Account", "Signed out")],
                };

            case NestMapState.Offline:
            {
                var detail = s.DetailText;
                return model with
                {
                    Glyph = OverviewIcon.CloudOff,
                    GlyphTone = OverviewTone.Neutral,
                    Title = "Can’t reach your nest",
                    Detail = detail.EndsWith('.') ? detail : $"{hostText} isn’t answering. Pairnets keeps trying by itself.",
                    HeadButton = new OverviewButton("Try now", OverviewIcon.Sync, OverviewAction.SyncNow, OverviewButtonKind.Primary),
                    Tiles =
                    [
                        new OverviewTile("Next try", "In a few seconds", "Pairnets keeps trying by itself"),
                        new OverviewTile("Waiting here", s.PendingChanges > 0 ? Count(s.PendingChanges, "change") : "Nothing yet", "They go up when the nest is back"),
                        new OverviewTile("Last connected", lastSynced is null ? "Not yet" : Capital(lastSynced), "Files here work as usual"),
                    ],
                    Lower = OverviewLower.ChangesAndFacts,
                    Changes = changes,
                    Facts = FactsOf(open, new OverviewFact(OverviewIcon.Server, "Your nest", hostText)),
                };
            }

            case NestMapState.Paused:
            {
                var infos = new List<OverviewInfo> { new(OverviewIcon.Clock, "Syncing is paused") };
                if (s.PendingChanges > 0)
                    infos.Add(new OverviewInfo(OverviewIcon.Up, Count(s.PendingChanges, "change") + " waiting here"));
                infos.Add(new OverviewInfo(OverviewIcon.Computer, "Your other computers keep syncing"));
                return model with
                {
                    Glyph = OverviewIcon.Pause,
                    GlyphTone = OverviewTone.Neutral,
                    Title = "Paused",
                    Detail = "Nothing syncs until you resume. Changes you make here are kept safe and go up when you press Resume.",
                    HeadButton = new OverviewButton("Resume", OverviewIcon.Play, OverviewAction.TogglePause, OverviewButtonKind.Primary, Large: true),
                    Infos = infos,
                    Lower = OverviewLower.ChangesAndFacts,
                    Changes = changes,
                    Facts = FactsOf(open, version, space),
                };
            }

            default:
                return model with
                {
                    Border = OverviewTone.Warn,
                    Glyph = OverviewIcon.FolderQuestion,
                    GlyphTone = OverviewTone.Warn,
                    Title = "Can’t find your sync folder",
                    Badge = "Stopped",
                    BadgeTone = OverviewTone.Warn,
                    Detail = "Maybe a drive is unplugged, or the folder was moved or renamed. Pairnets stopped, so nothing on your nest gets deleted by mistake.",
                    FolderBox = new OverviewFolderBox(folderText, lastSynced is null ? "Not found" : $"Not found · last seen {lastSynced}", "Missing"),
                    Buttons =
                    [
                        new OverviewButton("Locate the sync folder…", OverviewIcon.Search, OverviewAction.Fix, OverviewButtonKind.Primary),
                        new OverviewButton("Look again", OverviewIcon.Sync, OverviewAction.SyncNow, OverviewButtonKind.Secondary),
                    ],
                    ButtonNote = "Plugged the drive back in? Pairnets notices by itself.",
                    Lower = OverviewLower.Strip,
                    Facts =
                    [
                        new OverviewFact(OverviewIcon.FolderFill, "Your folder · missing", folderText, OverviewTone.Warn, "Locate…", OverviewAction.Fix),
                        .. FactsOf(version),
                    ],
                };
        }
    }

    // ------------------------------------------------------------------ pieces

    private static OverviewModel Stopped(OverviewModel model, StatusSnapshot s, OverviewFact open, string? lastSynced)
    {
        var isDelete = s.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty;
        var details = new List<OverviewFact> { open };
        if (lastSynced is not null)
            details.Add(new OverviewFact(OverviewIcon.Clock, "Last synced", Capital(lastSynced)));
        if (isDelete)
            details.Add(new OverviewFact(OverviewIcon.Document, "Deleted files", "Kept 30 days in History"));
        if (!isDelete)
        {
            var title = s.Status == RunnerStatus.Error ? "Something went wrong" : s.Headline;
            var text = s.Text.Trim();
            return model with
            {
                Border = OverviewTone.Warn,
                Glyph = s.Status == RunnerStatus.Error ? OverviewIcon.Warning : OverviewIcon.Shield,
                GlyphTone = OverviewTone.Warn,
                Title = title,
                Badge = s.Status == RunnerStatus.Error ? "Stopped" : "Needs your OK",
                BadgeTone = OverviewTone.Warn,
                Detail = text,
                Buttons = s.FixLabel is { } label ? [new OverviewButton(label, OverviewIcon.Check, OverviewAction.Fix, OverviewButtonKind.Warn)] : [],
                Lower = OverviewLower.Strip,
                Facts = details,
            };
        }

        var count = s.PendingDeletes;
        var lines = s.PendingDeletePaths.Take(4).Select(p =>
            new OverviewFileLine(Paths.PathRules.FileName(p), Paths.PathRules.Parent(p) is { Length: > 0 } parent ? Paths.PathRules.FileName(parent) : "In your folder", IconFor(p))).ToList();
        var more = count > lines.Count && lines.Count > 0 ? $"and {Format.Count(count - lines.Count)} more" : null;
        return model with
        {
            Border = OverviewTone.Warn,
            Glyph = OverviewIcon.Shield,
            GlyphTone = OverviewTone.Warn,
            Title = "Stopped to keep your files safe",
            Badge = "Needs your OK",
            BadgeTone = OverviewTone.Warn,
            Detail = count > 0
                ? $"This sync would delete {Count(count, "file")}. Nothing was deleted, and nothing happens until you decide."
                : "This sync would delete files. Nothing was deleted, and nothing happens until you decide.",
            DeleteBox = lines.Count == 0 ? null : new OverviewDeleteBox("Would be deleted from your nest", Count(count, "file"), lines, more),
            Buttons =
            [
                new OverviewButton($"Allow these deletions ({Format.Count(count)})…", OverviewIcon.Check, OverviewAction.Fix, OverviewButtonKind.Warn),
                new OverviewButton("See the full list", OverviewIcon.List, OverviewAction.SeeList, OverviewButtonKind.Secondary),
            ],
            ButtonNote = "If you allow it, the files stay in History for 30 days.",
            Lower = OverviewLower.Strip,
            Facts = details,
        };
    }

    private static IReadOnlyList<OverviewFact> FactsOf(params OverviewFact?[] facts) => facts.Where(f => f is not null).Select(f => f!).ToList();

    private static OverviewFact? VersionFact(StatusSnapshot s) => s.Server is null
        ? null
        : new OverviewFact(OverviewIcon.Server, "Nest version", s.Server.ServerVersion is { Length: > 0 } v ? v : "Old version",
            s.ServerIsOlder ? OverviewTone.Warn : OverviewTone.Neutral, s.ServerUpdateButtonText, OverviewAction.CheckNestUpdate);

    private static string Count(int n, string noun) => Format.Count(n) + " " + noun + (n == 1 ? string.Empty : "s");

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"today 10:45" → "today at 10:45".</summary>
    private static string At(string moment) => moment.Length > 5 ? moment[..^5] + "at " + moment[^5..] : moment;

    private static string NeedsText(StatusSnapshot s, int attentionCount)
    {
        if (attentionCount <= 0)
            return string.Empty;
        var joins = Math.Min(s.JoinRequests.Count, attentionCount);
        var others = attentionCount - joins;
        var parts = new List<string>();
        if (joins == 1)
            parts.Add("A computer asks to join");
        else if (joins > 1)
            parts.Add($"{joins.ToString(CultureInfo.InvariantCulture)} computers ask to join");
        if (others == 1)
            parts.Add("1 item needs a look");
        else if (others > 1)
            parts.Add($"{others.ToString(CultureInfo.InvariantCulture)} items need a look");
        return parts.Count == 2 ? parts[0] + ", and " + parts[1].ToLowerInvariant() + "." : parts[0] + ".";
    }

    /// <summary>Which way the files go: from what is moving, or "checking" while the folder is read.</summary>
    public static OverviewFlow FlowOf(StatusSnapshot s)
    {
        if (s.Status != RunnerStatus.Syncing)
            return OverviewFlow.None;
        if (s.Motion == SyncMotion.Stalled)
            return OverviewFlow.None;
        if (s.GoingUp && s.GoingDown)
            return OverviewFlow.Both;
        if (s.GoingUp)
            return OverviewFlow.Up;
        if (s.GoingDown)
            return OverviewFlow.Down;
        return OverviewFlow.Checking;
    }

    /// <summary>"Uploading 120 files to your nest", "Downloading big.bin from your nest", or the status words.</summary>
    public static string SyncingTitle(StatusSnapshot s)
    {
        var headline = s.Headline;
        if (!s.IsTransferring)
            return headline;
        var what = s.FilesTotal > 1 ? Format.Count(s.FilesTotal) + " files" : s.CurrentFileName ?? "a file";
        return headline switch
        {
            "Uploading" => $"Uploading {what} to your nest",
            "Downloading" => $"Downloading {what} from your nest",
            "Uploading and downloading" => $"Syncing {what} with your nest",
            _ => headline,
        };
    }

    private static OverviewProgress SyncingProgress(StatusSnapshot s, DateTimeOffset now)
    {
        if (s.IsTransferring)
        {
            var stall = s.StallText(now);
            var limit = s.LimitText;
            var speed = s.SpeedText;
            string right;
            if (stall.Length > 0)
                right = stall;
            else if (limit is { Length: > 0 } && s.BytesPerSecond >= 1 && s.PassBytesTotal - s.PassBytesDone > 0)
                right = limit + " · " + Format.Duration(TimeSpan.FromSeconds((s.PassBytesTotal - s.PassBytesDone) / s.BytesPerSecond)) + " left";
            else
                right = limit is { Length: > 0 } && speed.Length == 0 ? limit : speed;
            return new OverviewProgress(s.OverallPercent, s.OverallText, right, stall.Length > 0 || s.IsSlowedDown);
        }

        var percent = s.CheckTotal > 0 ? (int)Math.Clamp(s.CheckedFiles * 100L / s.CheckTotal, 0, 100) : (int?)null;
        return new OverviewProgress(percent, s.DetailAt(now), s.IsSlowedDown ? s.SlowedText(now) : string.Empty, s.IsSlowedDown);
    }

    private static string MovingCountText(StatusSnapshot s)
    {
        var moving = s.Batch.Count(f => f.IsMoving);
        if (moving == 0)
            moving = s.Active.Count;
        return s.FilesTotal > 0 ? $"{Format.Count(moving)} of {Format.Count(s.FilesTotal)} files" : moving == 0 ? string.Empty : Count(moving, "file");
    }

    private static IReadOnlyList<OverviewMovingRow> MovingRows(StatusSnapshot s)
    {
        var rows = new List<OverviewMovingRow>();
        foreach (var g in TransferGroups.Build(s, max: 5))
        {
            if (g.IsFolder)
            {
                var name = Paths.PathRules.FileName(g.Key);
                var parent = Paths.PathRules.Parent(g.Key);
                var total = g.Files.Count;
                var movingNow = g.Files.Count(f => f.IsMoving);
                var sub = (parent is { Length: > 0 } ? Paths.PathRules.FileName(parent) + " · " : string.Empty)
                          + Count(total, "file") + (movingNow > 0 ? $" · {Format.Count(movingNow)} moving" : string.Empty);
                var files = (movingNow > 0 ? g.Files.Where(f => f.IsMoving) : g.Files.Where(f => f.IsWaiting)).Take(4)
                    .Select(f => new OverviewMovingFile(f.FileName, f.IsMoving ? f.Percent : null, f.StateText, IconFor(f.Path))).ToList();
                rows.Add(new OverviewMovingRow(g.Key, name, sub, g.Percent, true, OverviewIcon.FolderFill, files));
            }
            else
            {
                rows.Add(new OverviewMovingRow(g.Key, g.Title, string.Empty, g.Percent, false, IconFor(g.Title), []));
            }
        }
        return rows;
    }

    private static IReadOnlyList<OverviewChange> ChangesOf(IReadOnlyList<ActivityItem> recent, DateTimeOffset now)
    {
        var list = new List<OverviewChange>();
        foreach (var item in recent.Take(3))
        {
            var (icon, tone) = item.Kind switch
            {
                ActivityKind.Uploaded => (OverviewIcon.Up, OverviewTone.Accent),
                ActivityKind.Downloaded => (OverviewIcon.Down, OverviewTone.Ok),
                ActivityKind.Blocked => (OverviewIcon.Pause, OverviewTone.Warn),
                ActivityKind.Offline => (OverviewIcon.CloudOff, OverviewTone.Neutral),
                ActivityKind.Warning or ActivityKind.Error or ActivityKind.Conflict => (OverviewIcon.Warning, OverviewTone.Warn),
                _ => (OverviewIcon.Info, OverviewTone.Neutral),
            };
            list.Add(new OverviewChange(icon, tone, item.Primary, item.Title, item.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>A picture for a file from its name: image, video or a plain document.</summary>
    public static OverviewIcon IconFor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".svg" or ".tif" or ".tiff" or ".raw" => OverviewIcon.Image,
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" or ".wmv" => OverviewIcon.Video,
            ".md" or ".txt" or ".doc" or ".docx" or ".pdf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".csv" or ".rtf" or ".odt" => OverviewIcon.Document,
            _ => OverviewIcon.File,
        };
    }
}
