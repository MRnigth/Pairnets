using System.Globalization;

namespace Pairnets.Core.Client;

/// <summary>Whether another computer can be reached, and if not, whether that is a problem.</summary>
public enum Presence
{
    /// <summary>It is online (or was heard from just now).</summary>
    Online,

    /// <summary>Simply switched off or away: grey and dashed, "off since 10:45". Nothing is wrong.</summary>
    Off,

    /// <summary>
    /// A real problem: it went away in the middle of moving files (its last report said it was busy and it never said it
    /// stopped). Red and dashed with a cross, "can’t reach".
    /// </summary>
    Lost,
}

/// <summary>
/// The one rule for a computer that is not online, used by the nest picture, the left side, the tray and the Devices page
/// so they all say the same word in the same colour. A computer that stops syncing normally sends a last, empty report
/// (<see cref="Pairnets.Core.TransferReport"/>), so only one that vanished while busy is <see cref="Presence.Lost"/>; a
/// computer that is switched off or asleep is <see cref="Presence.Off"/>.
/// </summary>
public static class Presences
{
    /// <summary>How long a computer that vanished while busy counts as a problem; after that it is just off.</summary>
    public static readonly TimeSpan LostFor = TimeSpan.FromHours(1);

    public static Presence Of(StatusSnapshot s, string name, bool online, DateTimeOffset now)
    {
        if (online)
            return Presence.Online;
        foreach (var (device, peer) in s.Peers)
        {
            if (string.Equals(device, name, StringComparison.OrdinalIgnoreCase))
                return peer.Report is { IsActive: true } && now - peer.ReportAt <= LostFor ? Presence.Lost : Presence.Off;
        }
        return Presence.Off;
    }

    /// <summary>"off since 10:45" (today), "off since yesterday", "off since 9 Oct".</summary>
    public static string OffSince(DateTimeOffset lastSeen, DateTimeOffset now) => "off since " + Since(lastSeen, now);

    /// <summary>"10:45" (today), "yesterday", "9 Oct".</summary>
    public static string Since(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date >= today)
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today.AddDays(-1) ? "yesterday" : local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"3 of 6 online": the one way every place counts the computers.</summary>
    public static string OnlineCount(int online, int total) =>
        $"{online.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} online";
}
