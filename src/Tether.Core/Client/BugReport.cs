using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Tether.Core.Settings;

namespace Tether.Core.Client;

/// <summary>
/// "Report a bug": everything needed to troubleshoot, as plain text the user pastes to whoever helps
/// them. Nothing is sent anywhere, and the token is never included.
/// </summary>
public static class BugReport
{
    public const int LogLines = 300;
    public const int ActivityItems = 30;
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(10);

    // Anything token-like that might have reached a log line.
    private static readonly Regex TokenLike = new(@"((?:sync[_-]?token|x-sync-token|token)\s*[:=]\s*)\S+", RegexOptions.IgnoreCase);

    /// <param name="app">Which app wrote the report ("Windows app", "Mac app", …).</param>
    /// <param name="logFile">Today's log (<c>RollingFileLoggerProvider.CurrentFile</c>), or null.</param>
    /// <param name="error">The unexpected error that led to the report, or null when the user asked for it.</param>
    public static async Task<string> BuildAsync(ClientSettings settings, ClientSession? session, string? logFile, Exception? error,
        string app, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("Tether bug report");
        sb.AppendLine("=================");
        sb.AppendLine(inv, $"Created:   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(inv, $"App:       {app} {TetherInfo.ProductVersion}");
        sb.AppendLine(inv, $"System:    {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");

        if (error is not null)
        {
            sb.AppendLine().AppendLine("## Error");
            sb.AppendLine(error.ToString());
        }

        sb.AppendLine().AppendLine("## Settings (the token is never included)");
        sb.AppendLine(inv, $"Server:              {settings.ServerUrl ?? "(not set)"}");
        sb.AppendLine(inv, $"This computer:       {settings.DeviceName ?? "(not set)"}");
        sb.AppendLine(inv, $"Folder:              {settings.Folder ?? "(not set)"}");
        sb.AppendLine(inv, $"Paused:              {YesNo(settings.Paused)}");
        sb.AppendLine(inv, $"Start at sign-in:    {YesNo(settings.StartWithWindows)}");
        sb.AppendLine(inv, $"Check for updates:   {YesNo(settings.CheckForUpdates)}");
        sb.AppendLine(inv, $"Auto-update server:  {YesNo(settings.AutoUpdateServer)}");
        sb.AppendLine(inv, $"Skipped server ver.: {settings.SkippedServerVersion ?? "-"}");
        sb.AppendLine(inv, $"Wait for big batch:  {YesNo(settings.WaitForPeerBatches)}");
        sb.AppendLine(inv, $"Files at a time:     {settings.EffectiveParallelTransfers}");
        sb.AppendLine(inv, $"Speed limits:        {settings.LimitText ?? "none"}");
        sb.AppendLine(inv, $"Extra ignore rules:  {settings.ExtraIgnore.Count}");
        sb.AppendLine(inv, $"Debug mode:          {YesNo(settings.DebugMode)}");

        sb.AppendLine().AppendLine("## Status");
        if (session is null)
        {
            sb.AppendLine("No sync session is running (not set up yet, or stopped).");
        }
        else
        {
            var s = session.Status;
            sb.AppendLine(inv, $"State:        {s.Status} - {s.Headline}");
            sb.AppendLine(inv, $"Text:         {s.Text}");
            sb.AppendLine(inv, $"Connection:   {s.ConnectionText ?? "starting"}");
            sb.AppendLine(inv, $"Last sync:    {s.LastSyncAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "never"}");
            sb.AppendLine(inv, $"Blocked:      {s.BlockReason} (pending deletions: {s.PendingDeletes}, warnings: {s.Warnings})");
            sb.AppendLine(inv, $"Transfers:    {s.Active.Count} running");
            sb.AppendLine(inv, $"Server:       {s.Server?.ServerVersion ?? "unknown version"}, {s.ServerFreeText ?? "free space unknown"}");
            if (s.Server?.Updater is { } updater)
                sb.AppendLine(inv, $"Updater:      {updater.State}{(updater.Message is { } m ? " - " + m : string.Empty)}");
        }

        sb.AppendLine().AppendLine("## Server updater");
        if (session is null)
        {
            sb.AppendLine("(no session)");
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ServerTimeout);
            try
            {
                sb.AppendLine(await session.GetUpdaterDiagnosticsTextAsync(timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sb.AppendLine(inv, $"The server did not answer within {ServerTimeout.TotalSeconds:0} seconds.");
            }
        }

        sb.AppendLine().AppendLine(inv, $"## Recent activity (newest first, up to {ActivityItems})");
        var activity = session?.Activity.Items.Take(ActivityItems).ToList() ?? [];
        if (activity.Count == 0)
            sb.AppendLine("(none)");
        foreach (var item in activity)
            sb.AppendLine(inv, $"{item.Time.ToLocalTime():HH:mm:ss} {item.Kind,-10} {item.Text}");

        sb.AppendLine().AppendLine(inv, $"## Log (last {LogLines} lines of {(logFile is null ? "today's log" : Path.GetFileName(logFile))})");
        foreach (var line in ReadTail(logFile, LogLines))
            sb.AppendLine(line);

        return TokenLike.Replace(sb.ToString(), "$1(hidden)");
    }

    /// <summary>Saves the report as bug-report-yyyyMMdd-HHmmss.txt in <paramref name="directory"/> and returns its path.</summary>
    public static string Save(string report, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"bug-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report);
        return path;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static IEnumerable<string> ReadTail(string? path, int lines)
    {
        if (path is null || !File.Exists(path))
            return ["(no log file yet)"];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxBytes = 256 * 1024;
            var cut = stream.Length > maxBytes;
            if (cut)
                stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (cut && all.Count > 0)
                all.RemoveAt(0); // probably cut in the middle
            if (all.Count > 0 && all[^1].Length == 0)
                all.RemoveAt(all.Count - 1);
            return all.Count == 0 ? ["(the log is empty)"] : all.TakeLast(lines).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["(the log could not be read: " + ex.Message + ")"];
        }
    }
}
