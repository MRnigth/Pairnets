using System.Text.Json;

namespace Pairnets.Core.Client;

/// <summary>What <see cref="StartGuard.Begin"/> found out about the run before this one.</summary>
/// <param name="PreviousStoppedUnexpectedly">The run before this one never said goodbye: Pairnets crashed, was force-quit, or the whole Mac went down.</param>
/// <param name="LastAlive">When that run was last known to be running (its last health tick); null when it never ticked.</param>
/// <param name="RecentStops">How many unexpected stops there were in the last <see cref="StartGuard.LoopWindow"/>, this one included.</param>
/// <param name="CrashLoop">Pairnets stopped unexpectedly <see cref="StartGuard.LoopLimit"/> times or more in that time: start with syncing paused.</param>
public sealed record StartGuardResult(bool PreviousStoppedUnexpectedly, DateTimeOffset? LastAlive, int RecentStops, bool CrashLoop);

/// <summary>
/// Notices a run that ended without a clean quit. A small file ("running.json") is written when Pairnets starts, touched
/// every minute and removed on a clean quit; one that is still there at the next start means the last run was cut off,
/// also when nothing could be caught (a Force Quit, a freeze, a restart of the whole Mac). Three such stops within half
/// an hour are a crash loop: the next start comes up paused and says so.
/// </summary>
public sealed class StartGuard
{
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(30);
    public const int LoopLimit = 3;
    private const int KeepStops = 10;

    private readonly string _running;
    private readonly string _stops;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private bool _began;

    public StartGuard(string directory, TimeProvider? clock = null)
    {
        _running = Path.Combine(directory, "running.json");
        _stops = Path.Combine(directory, "unexpected-stops.json");
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record Running(DateTimeOffset Started, DateTimeOffset LastAlive, string Version);

    /// <summary>Looks at what the last run left, then marks this run as running. Never throws.</summary>
    public StartGuardResult Begin()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var stops = ReadStops();
            DateTimeOffset? lastAlive = null;
            var unexpected = false;
            try
            {
                if (File.Exists(_running) && JsonSerializer.Deserialize<Running>(File.ReadAllText(_running)) is { } before)
                {
                    unexpected = true;
                    lastAlive = before.LastAlive;
                    stops.Add(before.LastAlive);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A file nobody can read says nothing; treat it as a clean start.
            }
            stops = stops.Where(s => now - s < LoopWindow).OrderBy(s => s).TakeLast(KeepStops).ToList();
            WriteStops(stops);
            _began = true;
            Write(new Running(now, now, PairnetsInfo.ProductVersion));
            return new StartGuardResult(unexpected, lastAlive, stops.Count, stops.Count >= LoopLimit);
        }
    }

    /// <summary>Says "still running" (the health log does this every minute).</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (!_began)
                return;
            try
            {
                var before = File.Exists(_running) ? JsonSerializer.Deserialize<Running>(File.ReadAllText(_running)) : null;
                var now = _clock.GetUtcNow();
                Write(new Running(before?.Started ?? now, now, PairnetsInfo.ProductVersion));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Not worth an error.
            }
        }
    }

    /// <summary>A clean quit: the next start finds nothing left behind.</summary>
    public void CleanExit()
    {
        lock (_gate)
        {
            _began = false;
            try
            {
                File.Delete(_running);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Then the next start thinks this run was cut off, which only adds a note.
            }
        }
    }

    /// <summary>Forgets the earlier stops (the person pressed Resume): the next start is not a crash loop by their count.</summary>
    public void Forgive()
    {
        lock (_gate)
            WriteStops([]);
    }

    private List<DateTimeOffset> ReadStops()
    {
        try
        {
            if (File.Exists(_stops))
                return JsonSerializer.Deserialize<List<DateTimeOffset>>(File.ReadAllText(_stops)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start the list again.
        }
        return [];
    }

    private void WriteStops(List<DateTimeOffset> stops) => Save(_stops, JsonSerializer.Serialize(stops));

    private void Write(Running running) => Save(_running, JsonSerializer.Serialize(running));

    private static void Save(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break the app.
        }
    }
}

/// <summary>The words about a stop nobody saw coming, the same in both apps.</summary>
public static class UnexpectedStopText
{
    public const string Title = "Pairnets stopped unexpectedly";

    public static string Text(StartGuardResult r) => r.CrashLoop
        ? $"Pairnets stopped unexpectedly {r.RecentStops} times in the last half hour, so syncing is paused until you resume it. " +
          "Open Report a bug to send us a note about it (without your files or names); nothing is sent unless you press Send."
        : "The last time Pairnets ran, it stopped without closing properly. A note about it, without your files or names, " +
          "is in Report a bug, where you can send it to us. Nothing was sent.";

    /// <summary>The line on the crash card (cleaned like every card).</summary>
    public static string Card(StartGuardResult r) =>
        "Pairnets stopped without closing properly" + (r.LastAlive is { } at ? $" (last seen running {at.ToLocalTime():yyyy-MM-dd HH:mm})" : string.Empty)
        + $"; {r.RecentStops} unexpected stop{(r.RecentStops == 1 ? string.Empty : "s")} in the last half hour.";
}

/// <summary>The "error" behind the crash card for a run that was cut off with nothing caught.</summary>
public sealed class UnexpectedStopException(string message) : Exception(message);
