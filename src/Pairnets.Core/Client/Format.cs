using System.Globalization;

namespace Pairnets.Core.Client;

/// <summary>Human-friendly text for sizes and times, shared by all UIs.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"0 B", "512 B", "1.5 KB", "64.0 MB", "2.10 GB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.##";
        if (unit >= 2 && value < 10)
            format = "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>"just now", "1 min ago", "5 min ago", "2 h ago", "yesterday 14:05", "2 Oct 14:05".</summary>
    public static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromSeconds(45))
            return "just now";
        if (age < TimeSpan.FromMinutes(60))
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))} min ago";
        var local = time.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
            return $"{(int)age.TotalHours} h ago";
        if (local.Date == today.AddDays(-1))
            return "yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A point in time people can place: "today 14:02", "yesterday 18:30", "Mon 5 Oct 09:12", "5 Oct 2025 09:12".</summary>
    public static string Moment(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
            return "today " + clock;
        if (local.Date == today.AddDays(-1))
            return "yesterday " + clock;
        if (local.Date > today.AddDays(-7) && local.Date < today)
            return local.ToString("ddd d MMM ", CultureInfo.InvariantCulture) + clock;
        return local.ToString(local.Year == today.Year ? "d MMM " : "d MMM yyyy ", CultureInfo.InvariantCulture) + clock;
    }

    /// <summary>A heading for one day of activity: "Today", "Yesterday", "Monday 5 October", "5 October 2025".</summary>
    public static string Day(DateTime localDay, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        if (localDay.Date == today)
            return "Today";
        if (localDay.Date == today.AddDays(-1))
            return "Yesterday";
        return localDay.ToString(localDay.Year == today.Year ? "dddd d MMMM" : "d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>"12.4 MB/s", "820 KB/s".</summary>
    public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>"38,206": a count with thousands separators.</summary>
    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"↑ 3.10 MB/s", "↓ 4.20 MB/s", "↑ 1.00 MB/s ↓ 2.00 MB/s", or empty when nothing moves.</summary>
    public static string Flow(double up, double down) => (up >= 1, down >= 1) switch
    {
        (true, true) => $"↑ {Speed(up)} ↓ {Speed(down)}",
        (true, false) => "↑ " + Speed(up),
        (false, true) => "↓ " + Speed(down),
        _ => string.Empty,
    };

    /// <summary>A computer's live report in a few words: "↑ 3.10 MB/s · 1,204 files left", "↓ 4.20 MB/s".</summary>
    public static string Live(TransferReport report)
    {
        var flow = Flow(report.UpBytesPerSecond, report.DownBytesPerSecond);
        var left = report.FilesLeft switch
        {
            0 => string.Empty,
            1 => "1 file left",
            var n => Count(n) + " files left",
        };
        return flow.Length > 0 && left.Length > 0 ? flow + " · " + left : flow + left;
    }

    /// <summary>"less than a minute", "about 2 min", "about 1 h 20 min".</summary>
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.FromMinutes(1))
            return "less than a minute";
        if (time < TimeSpan.FromHours(1))
            return $"about {(int)Math.Ceiling(time.TotalMinutes)} min";
        var minutes = (int)Math.Round(time.TotalMinutes) % 60;
        return minutes == 0 ? $"about {(int)time.TotalHours} h" : $"about {(int)time.TotalHours} h {minutes} min";
    }

    /// <summary>Megabytes per second as the user typed it: "5 MB/s", "0.5 MB/s".</summary>
    public static string Megabytes(double mbps) => mbps.ToString("0.##", CultureInfo.InvariantCulture) + " MB/s";
}
