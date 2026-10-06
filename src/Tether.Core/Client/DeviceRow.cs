using System.Globalization;

namespace Tether.Core.Client;

/// <summary>One line in the Devices tab: "MacBook (this computer)", "Online", "macOS · Tether 1.0.38 · last change 5 min ago".</summary>
public sealed record DeviceRow(string Name, bool IsThisComputer, bool Online, string StatusText, string Detail)
{
    public string Title => IsThisComputer ? Name + " (this computer)" : Name;

    /// <summary>This computer first, then the online ones, then by when they were last seen.</summary>
    public static IReadOnlyList<DeviceRow> From(IReadOnlyList<DeviceInfo> devices, string? thisDevice, DateTimeOffset now) =>
        devices
            .Select(d => (Device: d, IsThis: string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.IsThis)
            .ThenByDescending(x => x.Device.Online)
            .ThenByDescending(x => x.Device.LastSeen)
            .Select(x => new DeviceRow(x.Device.Name, x.IsThis, x.Device.Online,
                x.Device.Online ? "Online" : "Last seen " + Ago(now - x.Device.LastSeen),
                DetailFor(x.Device, now)))
            .ToList();

    private static string DetailFor(DeviceInfo d, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (d.System is { Length: > 0 } system)
            parts.Add(system);
        if (d.AppVersion is { Length: > 0 } version)
            parts.Add("Tether " + version);
        parts.Add(d.LastChange is { } change ? "last change " + Ago(now - change) : "no changes yet");
        parts.Add("added " + d.FirstSeen.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "1 day ago", "12 days ago".</summary>
    public static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} min ago",
        < 60 * 24 => $"{(int)span.TotalHours} h ago",
        < 60 * 48 => "1 day ago",
        _ => $"{(int)span.TotalDays} days ago",
    };
}
