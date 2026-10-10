using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>How a box in the picture looks: green dot (online), grey (offline), dashed (not known yet).</summary>
public enum NodeState
{
    Online,
    Offline,
    Unknown,
}

/// <summary>Which way files are moving on a line. "Up" is towards the server.</summary>
public enum LinkFlow
{
    None,
    Up,
    Down,
    Both,
}

/// <summary>What the other computer is doing, as the server reports it (its circle breathes or gets a check mark).</summary>
public enum PeerState
{
    None,

    /// <summary>Uploading to the server.</summary>
    Sending,

    /// <summary>Downloading from the server.</summary>
    Downloading,

    /// <summary>It finished a sync that includes this computer's latest change.</summary>
    HasChanges,
}

/// <summary>One box in the picture: a computer or the server.</summary>
public sealed record MapNode(string Name, string Detail, NodeState State, string? Tip = null);

/// <summary>
/// "This computer ⇄ server ⇄ your other computer": what the overview and the tray panel draw.
/// Built from the status snapshot, so both apps show the same thing.
/// </summary>
public sealed record DeviceMap(
    MapNode Here,
    MapNode Server,
    MapNode Other,
    bool HereLinked,
    LinkFlow HereFlow,
    bool OtherLinked,
    LinkFlow OtherFlow,
    int MoreComputers)
{
    /// <summary>A change from the other computer within this time counts as "sending now".</summary>
    public static readonly TimeSpan SendingWindow = TimeSpan.FromSeconds(8);

    /// <summary>Computers not seen for this long are left out (renamed or retired ones).</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(60);

    /// <summary>"Has your changes" shows this long after the other computer said so.</summary>
    public static readonly TimeSpan HasChangesWindow = TimeSpan.FromMinutes(1);

    /// <summary>Dot speed (pixels per second) when files move but the speed isn't known (an older server); today's pace.</summary>
    public const double DefaultDotSpeed = 34 / 1.3;

    /// <summary>This computer's speed each way (bytes per second).</summary>
    public double HereUpSpeed { get; init; }

    public double HereDownSpeed { get; init; }

    /// <summary>Files should move on this computer's line but nothing has for a few seconds: the dots stop and fade.</summary>
    public bool HereStalled { get; init; }

    /// <summary>The other computer's speed each way as the server measures it, or null when the server doesn't say.</summary>
    public double? OtherUpSpeed { get; init; }

    public double? OtherDownSpeed { get; init; }

    public PeerState OtherState { get; init; }

    /// <summary>"↑ 4.9 MB/s", "↓ 850 KB/s" or "↑ 3.1 MB/s · ↓ 2 MB/s" on this computer's line, or null while nothing moves.</summary>
    public string? HereRateText => RateText(HereFlow, HereUpSpeed, HereDownSpeed, HereStalled);

    /// <summary>The same for the other computer's line (null when its speed isn't known).</summary>
    public string? OtherRateText => RateText(OtherFlow, OtherUpSpeed, OtherDownSpeed, stalled: false);

    private static string? RateText(LinkFlow flow, double? up, double? down, bool stalled)
    {
        string? Part(string arrow, double? speed) => speed is { } v && (v >= 1 || stalled) ? arrow + " " + Format.Speed(v) : null;
        var parts = flow switch
        {
            LinkFlow.Up => [Part("↑", up)],
            LinkFlow.Down => [Part("↓", down)],
            LinkFlow.Both => new[] { Part("↑", up), Part("↓", down) },
            _ => [],
        };
        var text = string.Join(" · ", parts.OfType<string>());
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// How fast the dots travel (pixels per second) for a real speed: faster with more megabytes, still for none.
    /// Null (the speed isn't known) moves them at the old steady pace.
    /// </summary>
    public static double DotSpeed(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } bps)
            return DefaultDotSpeed;
        if (bps < 1)
            return 0;
        var megabytes = bps / (1024.0 * 1024);
        return Math.Clamp(14 + 30 * Math.Log2(1 + megabytes), 0, 175);
    }

    /// <summary>"+1 more computer" under the other computer's box, or empty.</summary>
    public string MoreText => MoreComputers switch
    {
        0 => string.Empty,
        1 => "+1 more computer",
        _ => $"+{MoreComputers} more computers",
    };

    public static DeviceMap Build(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        var connected = s.IsConnected;
        var here = new MapNode(string.IsNullOrWhiteSpace(thisDevice) ? "This computer" : thisDevice, "This computer",
            NodeState.Online, AppText(ThisSystem, PairnetsInfo.ProductVersion));

        var server = s.Server is null && !connected
            ? new MapNode("Server", s.Status == RunnerStatus.Offline ? "Can't reach it" : "Connecting…", s.Status == RunnerStatus.Offline ? NodeState.Offline : NodeState.Unknown)
            : connected
                ? new MapNode("Server", s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " free" : "Online", NodeState.Online, s.ServerVersionText)
                : new MapNode("Server", "Can't reach it", NodeState.Offline, s.ServerVersionText);

        var (other, more) = OtherComputer(s, thisDevice, now);

        var hereFlow = LinkFlow.None;
        if (connected && s.Status == RunnerStatus.Syncing)
        {
            var (up, down) = (s.GoingUp, s.GoingDown);
            hereFlow = up && down ? LinkFlow.Both : up ? LinkFlow.Up : down ? LinkFlow.Down : LinkFlow.None;
        }

        // The other computer's line shows only what the server reports (or, from an older server, the changes
        // heard from it), never a guess from what this computer does.
        var otherFlow = LinkFlow.None;
        var peer = s.Peers.TryGetValue(other.Name, out var p) ? p : PeerActivity.None;
        var moving = peer.IsMoving(now);
        double? otherUp = null, otherDown = null;
        var otherState = PeerState.None;
        if (connected && other.State == NodeState.Online)
        {
            var receiving = moving && peer.Transfer!.Downloading;
            var reportedSending = moving && peer.Transfer!.Uploading;
            var sending = reportedSending
                          || s.WaitingFor is { } w && string.Equals(w.Device, other.Name, StringComparison.OrdinalIgnoreCase)
                          || s.HeardFrom.TryGetValue(other.Name, out var at) && now - at <= SendingWindow;
            otherFlow = sending && receiving ? LinkFlow.Both : sending ? LinkFlow.Up : receiving ? LinkFlow.Down : LinkFlow.None;
            if (moving)
                (otherUp, otherDown) = (peer.Transfer!.UpBytesPerSecond, peer.Transfer.DownBytesPerSecond);
            otherState = receiving ? PeerState.Downloading
                : sending ? PeerState.Sending
                : HasMyChanges(s, peer, now) ? PeerState.HasChanges
                : PeerState.None;
            other = other with
            {
                Detail = otherState switch
                {
                    PeerState.Downloading => s.MyChangeVersion > peer.SyncedVersion ? "Downloading your changes" : "Downloading",
                    PeerState.HasChanges => "Has your changes",
                    PeerState.Sending when reportedSending && !other.Detail.StartsWith("Uploading ", StringComparison.Ordinal) => "Sending changes",
                    _ => other.Detail,
                },
            };
        }

        return new DeviceMap(here, server, other, connected, hereFlow, connected && other.State == NodeState.Online, otherFlow, more)
        {
            HereUpSpeed = s.UploadBytesPerSecond,
            HereDownSpeed = s.DownloadBytesPerSecond,
            HereStalled = s.Motion == SyncMotion.Stalled,
            OtherUpSpeed = otherUp,
            OtherDownSpeed = otherDown,
            OtherState = otherState,
        };
    }

    /// <summary>The other computer finished a sync that includes this computer's latest change, a moment ago.</summary>
    private static bool HasMyChanges(StatusSnapshot s, PeerActivity peer, DateTimeOffset now) =>
        s.MyChangeVersion > 0 && peer.SyncedVersion >= s.MyChangeVersion && peer.SyncedAt is { } at && now - at <= HasChangesWindow;

    private static (MapNode Node, int More) OtherComputer(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        if (s.DevicesUnsupported)
            return (new MapNode("Other computer", "Update the server to see it", NodeState.Unknown), 0);
        if (s.Devices is null)
            return (new MapNode("Other computer", s.IsConnected ? "Looking…" : "Unknown while offline", NodeState.Unknown), 0);

        var others = s.Devices
            .Where(d => !string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase) && (d.Online || now - d.LastSeen <= ForgetAfter))
            .OrderByDescending(d => d.Online || s.HeardFrom.ContainsKey(d.Name))
            .ThenByDescending(d => d.LastSeen)
            .ToList();
        if (others.Count == 0)
            return (new MapNode("Your other computer", "Not connected yet", NodeState.Unknown, "Install Pairnets on it, type your nest's name and press \"Sign in with your browser\". You approve it on your nest."), 0);

        var d = others[0];
        // A change that just arrived from it means it is online, even before the next poll says so.
        var online = d.Online || (s.HeardFrom.TryGetValue(d.Name, out var heard) && now - heard <= TimeSpan.FromMinutes(1))
                     || (s.Peers.TryGetValue(d.Name, out var peer) && peer.IsMoving(now));
        string detail;
        if (s.WaitingFor is { } wait && string.Equals(wait.Device, d.Name, StringComparison.OrdinalIgnoreCase))
            detail = $"Uploading {wait.Count} files";
        else if (online)
            detail = s.HeardFrom.TryGetValue(d.Name, out var at) && now - at <= SendingWindow ? "Sending changes" : "Online";
        else
            detail = "Last seen " + Format.RelativeTime(d.LastSeen, now);
        return (new MapNode(d.Name, detail, online ? NodeState.Online : NodeState.Offline, AppText(d.System, d.AppVersion)), others.Count - 1);
    }

    private static string ThisSystem => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>"Windows · Pairnets 1.0.58" (the tooltip on a computer's name), or null when the server does not say.</summary>
    private static string? AppText(string? system, string? version) =>
        (system, version) switch
        {
            ({ Length: > 0 } sys, { Length: > 0 } v) => $"{sys} · Pairnets {v}",
            ({ Length: > 0 } sys, _) => sys,
            (_, { Length: > 0 } v) => "Pairnets " + v,
            _ => null,
        };
}
