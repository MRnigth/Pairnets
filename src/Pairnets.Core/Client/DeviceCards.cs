using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>How a computer's card on the Devices page is coloured.</summary>
public enum DeviceCardTone
{
    /// <summary>Reachable and idle or in sync (green dot).</summary>
    Online,

    /// <summary>Moving files right now (blue speed note).</summary>
    Moving,

    /// <summary>Switched off or away: dashed grey icon, "Off since yesterday" (<see cref="Presence.Off"/>).</summary>
    Offline,

    /// <summary>A real problem: it vanished while moving files. Dashed red icon, "Can’t reach" (<see cref="Presence.Lost"/>).</summary>
    Unreachable,
}

/// <summary>One computer's card on the Devices page, as text ready to show.</summary>
/// <param name="Name">"DESKTOP".</param>
/// <param name="SystemLine">"Windows · 1.0.48" (what is known of it).</param>
/// <param name="StatusText">"Online", "Off since yesterday" or "Can’t reach".</param>
/// <param name="StatusNote">"in sync", "↓ 4.10 MB/s" or empty.</param>
/// <param name="LastChangeText">"Last change today 10:45" or "No changes yet".</param>
/// <param name="CanChooseIcon">The nest knows icons and this computer has an id: the pencil may show (the pencil shows).</param>
public sealed record DeviceCard(
    string Name,
    string? Id,
    bool IsThis,
    DeviceCardTone Tone,
    DeviceIconKind Icon,
    bool CanChooseIcon,
    string SystemLine,
    string StatusText,
    string StatusNote,
    string LastChangeText)
{
    public bool Online => Tone is DeviceCardTone.Online or DeviceCardTone.Moving;

    /// <summary>Red: it can't be reached and that is a problem (not just switched off).</summary>
    public bool Problem => Tone == DeviceCardTone.Unreachable;

    public string IconLabel => "Change the icon for " + Name;

    public string MoreLabel => "More for " + Name;

    /// <summary>The cards for the Devices page: this computer first, then the online ones, then by when they were last seen.</summary>
    public static IReadOnlyList<DeviceCard> From(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        if (s.Devices is not { } devices)
            return [];
        var side = SidePanelModel.Build(s, thisDevice, 0, 0, null, now);
        var others = side.AllOthers.ToDictionary(o => o.Name, o => o, StringComparer.OrdinalIgnoreCase);
        return devices
            .Select(d => (Device: d, IsThis: string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.IsThis)
            .ThenByDescending(x => x.Device.Online)
            .ThenByDescending(x => x.Device.LastSeen)
            .Select(x =>
            {
                var d = x.Device;
                var live = s.LiveOf(d.Name, thisDevice, now);
                var online = d.Online || live is not null;
                var note = string.Empty;
                if (live is not null)
                    note = Format.Live(live);
                else if (online && (x.IsThis ? s.Status == Sync.RunnerStatus.Idle : others.TryGetValue(d.Name, out var other) && other.Kind == OtherKind.Ok))
                    note = "in sync";
                var presence = x.IsThis ? Presence.Online : Presences.Of(s, d.Name, online, now);
                var tone = online ? live is not null ? DeviceCardTone.Moving : DeviceCardTone.Online
                    : presence == Presence.Lost ? DeviceCardTone.Unreachable : DeviceCardTone.Offline;
                var status = tone switch
                {
                    DeviceCardTone.Unreachable => "Can’t reach",
                    DeviceCardTone.Offline => "Off since " + Presences.Since(d.LastSeen, now),
                    _ => "Online",
                };
                return new DeviceCard(d.Name, d.Id, x.IsThis, tone,
                    DeviceIcon.For(d, s.DeviceIconsSupported), s.DeviceIconsSupported && d.Id is not null,
                    DescribeSystem(d), status, note,
                    d.LastChange is { } change ? "Last change " + Format.Moment(change, now) : "No changes yet");
            })
            .ToList();
    }

    private static string DescribeSystem(DeviceInfo d)
    {
        var parts = new List<string>();
        if (d.System is { Length: > 0 } system)
            parts.Add(system);
        if (d.AppVersion is { Length: > 0 } version)
            parts.Add(version);
        return parts.Count == 0 ? "Pairnets" : string.Join(" · ", parts);
    }

    /// <summary>"just now", "5 min ago", "today 10:45", "yesterday", "3 days ago", "9 Oct".</summary>
    public static string Seen(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
            return "today " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(-1))
            return "yesterday";
        var days = (int)(today - local.Date).TotalDays;
        return days < 14 ? $"{days} days ago" : local.ToString("d MMM", CultureInfo.InvariantCulture);
    }
}

/// <summary>The big "Your nest" card at the top of the Devices page.</summary>
/// <param name="Address">"nest.example.com", or "Not connected".</param>
/// <param name="Version">"1.0.48", or a dash.</param>
/// <param name="VersionNote">"up to date", "update available" or empty.</param>
/// <param name="Title">The nest's name ("soro") once its owner gave it one on their account page, else "Your nest".</param>
public sealed record NestCard(bool Online, string OnlineText, string Address, string Version, string VersionNote, bool VersionOlder, string FreeSpace, bool SpaceLow, string Keeps,
    string Title = "Your nest")
{
    public static NestCard From(StatusSnapshot s, string? serverUrl)
    {
        // A nest reached through sync.pairnets.app has no website of its own: name it by the address this computer uses.
        var host = Host(s.NestUrl) ?? Host(serverUrl) ?? Host(s.ServerUrl);
        var online = s.IsConnected;
        var version = s.Server?.ServerVersion is { Length: > 0 } v ? v : null;
        return new NestCard(
            online, online ? "Online" : s.BlockReason == BlockReason.SignedOutByYou ? "Signed out" : "Can't reach",
            host ?? "Not connected",
            version ?? "–",
            version is null ? string.Empty : s.ServerIsOlder ? "update available" : "up to date",
            s.ServerIsOlder,
            s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) : "–",
            s.ServerSpaceLow,
            HistoryQuery.Retention.Days.ToString(CultureInfo.InvariantCulture) + " days",
            s.NestTitle);
    }

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Authority : null;
}

/// <summary>The line above the cards and the "is asking to join" chip.</summary>
public static class DevicesSummary
{
    /// <summary>"3 of 6 online", the same words as the left side and its page tile.</summary>
    public static string Line(IReadOnlyList<DeviceCard> cards) =>
        cards.Count == 0 ? string.Empty : Presences.OnlineCount(cards.Count(c => c.Online), cards.Count);

    /// <summary>"PC-1 is asking to join", "2 computers are asking to join", or null.</summary>
    public static string? JoinText(StatusSnapshot s) => s.JoinRequests.Count switch
    {
        0 => null,
        1 => s.JoinRequests[0].Name + " is asking to join",
        var n => n.ToString(CultureInfo.InvariantCulture) + " computers are asking to join",
    };
}
