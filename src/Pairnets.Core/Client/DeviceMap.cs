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
    /// <summary>Computers not seen for this long are left out (renamed or retired ones).</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(60);

    /// <summary>"+1 more computer" under the other computer's box, or empty.</summary>
    public string MoreText => MoreComputers switch
    {
        0 => string.Empty,
        1 => "+1 more computer",
        _ => $"+{MoreComputers} more computers",
    };

    /// <summary>
    /// The picture. Every number on it is measured: this computer's own transfers, and what the other computer reported
    /// on the push channel in the last few seconds (<see cref="StatusSnapshot.LiveOf"/>). A line moves only while its
    /// computer really transfers; a computer that reports nothing shows no speed.
    /// </summary>
    public static DeviceMap Build(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        var connected = s.IsConnected;
        var own = s.OwnLive;
        var here = new MapNode(string.IsNullOrWhiteSpace(thisDevice) ? "This computer" : thisDevice, own is not null ? Format.Live(own) : "This computer",
            NodeState.Online, AppText(ThisSystem, PairnetsInfo.ProductVersion));

        var server = s.Server is null && !connected
            ? new MapNode("Server", s.Status == RunnerStatus.Offline ? "Can't reach it" : "Connecting…", s.Status == RunnerStatus.Offline ? NodeState.Offline : NodeState.Unknown)
            : connected
                ? new MapNode("Server", s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " free" : "Online", NodeState.Online, s.ServerVersionText)
                : new MapNode("Server", "Can't reach it", NodeState.Offline, s.ServerVersionText);

        var (other, live, more) = OtherComputer(s, thisDevice, now);

        // Held by "too many requests", nothing moves even though files wait in line.
        var hereFlow = LinkFlow.None;
        if (connected && s.Status == RunnerStatus.Syncing && !s.IsSlowedDown)
        {
            var up = s.Active.Any(a => a.IsUpload) || (s.Active.Count == 0 && s.Operation == "upload");
            var down = s.Active.Any(a => !a.IsUpload) || (s.Active.Count == 0 && s.Operation == "download");
            hereFlow = up && down ? LinkFlow.Both : up ? LinkFlow.Up : down ? LinkFlow.Down : LinkFlow.None;
        }

        var otherFlow = connected && other.State == NodeState.Online && live is not null ? FlowOf(live) : LinkFlow.None;
        return new DeviceMap(here, server, other, connected, hereFlow, connected && other.State == NodeState.Online, otherFlow, more);
    }

    /// <summary>Which way a computer's line moves for what it reported: "up" is towards the server.</summary>
    public static LinkFlow FlowOf(TransferReport report) => (report.UpBytesPerSecond >= 1, report.DownBytesPerSecond >= 1) switch
    {
        (true, true) => LinkFlow.Both,
        (true, false) => LinkFlow.Up,
        (false, true) => LinkFlow.Down,
        _ => LinkFlow.None,
    };

    private static (MapNode Node, TransferReport? Live, int More) OtherComputer(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        if (s.DevicesUnsupported)
            return (new MapNode("Other computer", "Update the server to see it", NodeState.Unknown), null, 0);
        if (s.Devices is null)
            return (new MapNode("Other computer", s.IsConnected ? "Looking…" : "Unknown while offline", NodeState.Unknown), null, 0);

        var others = s.Devices
            .Where(d => !string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase) && (d.Online || now - d.LastSeen <= ForgetAfter))
            .OrderByDescending(d => s.LiveOf(d.Name, thisDevice, now) is not null)
            .ThenByDescending(d => d.Online || s.HeardFrom.ContainsKey(d.Name))
            .ThenByDescending(d => d.LastSeen)
            .ToList();
        if (others.Count == 0)
            return (new MapNode("Your other computer", "Not connected yet", NodeState.Unknown, "Install Pairnets on it, type your nest's name and press \"Sign in with your browser\". You approve it on your nest."), null, 0);

        var d = others[0];
        var live = s.LiveOf(d.Name, thisDevice, now);
        // A report or a change that just arrived from it means it is online, even before the next poll says so.
        var online = d.Online || live is not null || (s.HeardFrom.TryGetValue(d.Name, out var heard) && now - heard <= TimeSpan.FromMinutes(1));
        string detail;
        if (live is not null)
            detail = Format.Live(live);
        else if (s.WaitingFor is { } wait && string.Equals(wait.Device, d.Name, StringComparison.OrdinalIgnoreCase))
            detail = $"Uploading {Format.Count(wait.Count)} files";
        else if (online)
            detail = "Online";
        else
            detail = "Last seen " + Format.RelativeTime(d.LastSeen, now);
        return (new MapNode(d.Name, detail, online ? NodeState.Online : NodeState.Offline, AppText(d.System, d.AppVersion)), live, others.Count - 1);
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
