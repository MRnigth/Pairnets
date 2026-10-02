using System.Globalization;

namespace Tether.Core.Client;

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
}
