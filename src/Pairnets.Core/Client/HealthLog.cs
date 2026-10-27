using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pairnets.Core.Client;

/// <summary>What only the app knows, for one health line.</summary>
/// <param name="UiStallMs">The longest wait of the window's thread in the last minute (<see cref="UiStallMeter"/>); -1 when not measured.</param>
public sealed record HealthExtras(int Watchers, int WatcherRestarts, int Passes, int NoticesWaiting, int NoticesHeldBack, IReadOnlyList<string> FreeDisk, int UiStallMs = -1);

/// <summary>
/// A small diagnostic log, one line a minute, in its own file (health-yyyyMMdd.log, at most
/// <see cref="MaxDailyBytes"/> a day, 14 days kept): how much memory, how many threads and open files, what the folder
/// watchers and passes are doing, how much space is free. When a Mac freezes or restarts, the last lines say whether
/// Pairnets was using up something just before. Holds nothing personal: numbers only, and drive names are left out.
/// </summary>
public sealed class HealthLog : IDisposable
{
    public const long MaxDailyBytes = 5L * 1024 * 1024;
    private const int RetentionDays = 14;

    private readonly string _directory;
    private readonly Func<HealthExtras> _extras;
    private readonly Func<long> _logBytesToday;
    private readonly TimeProvider _clock;
    private readonly Action? _tick;
    private readonly object _gate = new();
    private ITimer? _timer;

    /// <param name="logBytesToday">The size of today's main log (a log that grows fast shows here).</param>
    /// <param name="tick">Called after each line (the start guard's "still running").</param>
    public HealthLog(string directory, Func<HealthExtras> extras, Func<long> logBytesToday, Action? tick = null, TimeProvider? clock = null)
    {
        _directory = directory;
        _extras = extras;
        _logBytesToday = logBytesToday;
        _tick = tick;
        _clock = clock ?? TimeProvider.System;
    }

    public string FileFor(DateTimeOffset day) => Path.Combine(_directory, $"health-{day.ToLocalTime():yyyyMMdd}.log");

    public string CurrentFile => FileFor(_clock.GetUtcNow());

    /// <summary>Writes a line now and then every <paramref name="every"/> (a minute).</summary>
    public void Start(TimeSpan? every = null)
    {
        var period = every ?? TimeSpan.FromMinutes(1);
        lock (_gate)
            _timer ??= _clock.CreateTimer(_ => Sample(), null, TimeSpan.Zero, period);
    }

    /// <summary>Writes one line. Never throws.</summary>
    public void Sample()
    {
        try
        {
            var now = _clock.GetUtcNow();
            var line = Line(now, _extras(), _logBytesToday());
            Append(now, line);
        }
        catch (Exception)
        {
            // A health line that can't be made is not worth an error.
        }
        try
        {
            _tick?.Invoke();
        }
        catch (Exception)
        {
            // Same.
        }
    }

    /// <summary>The line for <paramref name="now"/>, with this process's numbers.</summary>
    public static string Line(DateTimeOffset now, HealthExtras x, long logBytesToday)
    {
        var inv = CultureInfo.InvariantCulture;
        long workingSet = 0;
        int threads = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            workingSet = me.WorkingSet64;
            threads = me.Threads.Count;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Left at 0.
        }
        var sb = new StringBuilder();
        sb.Append(inv, $"{now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.Append(inv, $" mem={Mb(workingSet)}MB gc={Mb(GC.GetTotalMemory(false))}MB threads={threads}");
        sb.Append(inv, $" pool={ThreadPool.ThreadCount}/queued={ThreadPool.PendingWorkItemCount} files={OpenFiles()}");
        sb.Append(inv, $" watchers={x.Watchers} restarts={x.WatcherRestarts} passes={x.Passes}");
        sb.Append(inv, $" notices={x.NoticesWaiting}/held={x.NoticesHeldBack} log={Mb(logBytesToday)}MB");
        if (x.UiStallMs >= 0)
            sb.Append(inv, $" ui-stall={x.UiStallMs}ms");
        if (x.FreeDisk.Count > 0)
            sb.Append(inv, $" free={string.Join(',', x.FreeDisk)}");
        return sb.ToString();
    }

    /// <summary>How many files, pipes and sockets this process has open (-1 when unknown).</summary>
    public static int OpenFiles()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var me = Process.GetCurrentProcess();
                return me.HandleCount;
            }
            return Directory.GetFileSystemEntries("/dev/fd").Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>"12.3GB of 460GB" style numbers for <see cref="HealthExtras.FreeDisk"/>: free GB.</summary>
    public static string FreeGb(Sync.DiskSpaceInfo? space) => space is { } s ? (s.Free / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + "GB" : "?";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture);

    private void Append(DateTimeOffset now, string line)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var file = FileFor(now);
            var isNew = !File.Exists(file);
            if (!isNew && new FileInfo(file).Length >= MaxDailyBytes)
                return;
            File.AppendAllText(file, line + Environment.NewLine);
            if (isNew)
                PurgeOld(now);
        }
    }

    private void PurgeOld(DateTimeOffset now)
    {
        var cutoff = now.ToLocalTime().Date.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "health-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next time.
            }
        }
    }

    /// <summary>The newest <paramref name="count"/> lines, over today's and earlier files (for the bug report).</summary>
    public static IReadOnlyList<string> Tail(string directory, int count)
    {
        var lines = new List<string>();
        try
        {
            if (!Directory.Exists(directory))
                return lines;
            foreach (var file in Directory.EnumerateFiles(directory, "health-*.log").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                var part = File.ReadAllLines(file);
                lines.InsertRange(0, part.TakeLast(count - lines.Count));
                if (lines.Count >= count)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What was read so far is enough.
        }
        return lines;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
