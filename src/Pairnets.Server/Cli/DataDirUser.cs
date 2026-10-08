using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Pairnets.Server.Cli;

/// <summary>
/// The maintenance commands must run as the user that owns the data folder (pairnets). auth.db uses SQLite's
/// write-ahead log: run with plain sudo, a command could leave root-owned -wal/-shm files the service can no
/// longer write; run as anybody else, it cannot open the files at all. So a command run as the wrong user
/// stops before it touches anything and says how to run it instead. Windows has no such check.
/// </summary>
public static class DataDirUser
{
    /// <summary>Where install.sh puts the server program.</summary>
    public const string DefaultProgram = "/opt/pairnets/pairnets-server";

    /// <summary>Who owns the data folder and who runs this command, by user name.</summary>
    public sealed record Users(string Owner, string Current);

    /// <summary>
    /// The real check on Linux and macOS: asks <c>stat</c> who owns the folder. Null on Windows, when the folder
    /// does not exist yet, or when either name cannot be told; then nothing is refused.
    /// </summary>
    public static Users? Probe(string dataDir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(dataDir))
            return null;
        var current = Environment.UserName; // the effective user: "root" under plain sudo
        var owner = OwnerOf(Path.GetFullPath(dataDir));
        return owner is null || current.Length == 0 ? null : new Users(owner, current);
    }

    /// <summary>The one-line refusal when the wrong user runs a command, or null when it may go ahead.</summary>
    public static string? Problem(Users? users, string dataDir, IReadOnlyList<string> args) =>
        users is { } u && !string.Equals(u.Owner, u.Current, StringComparison.Ordinal) ? RunAs(u.Owner, dataDir, args) : null;

    /// <summary>What to say when the data folder could not be opened (permission denied).</summary>
    public static string AccessDenied(Users? users, string dataDir, IReadOnlyList<string> args) =>
        users is { } u && string.Equals(u.Owner, u.Current, StringComparison.Ordinal)
            ? $"Some files in {dataDir} belong to another user (after a run with plain sudo?). Give them back with: sudo chown -R {u.Owner}: {dataDir}"
            : RunAs(users?.Owner ?? "pairnets", dataDir, args);

    /// <summary>"Run this as the pairnets user, who owns /var/lib/pairnets: sudo -u pairnets /opt/pairnets/pairnets-server owner-link"</summary>
    public static string RunAs(string owner, string dataDir, IReadOnlyList<string> args) =>
        $"Run this as the {owner} user, who owns {dataDir}: sudo -u {owner} {Program()} {string.Join(' ', args.Select(Quote))}";

    /// <summary>The errors an unreadable or unwritable data folder or database shows up as.</summary>
    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is SqliteException { SqliteErrorCode: 8 or 14 }; // SQLITE_READONLY, SQLITE_CANTOPEN

    private static string Program() =>
        Environment.ProcessPath is { } path && Path.GetFileName(path) == "pairnets-server" ? path : DefaultProgram;

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@%+,".Contains(c))
            ? arg
            : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? OwnerOf(string path)
    {
        var start = new ProcessStartInfo("stat")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // GNU (Linux) and BSD (macOS) stat spell "owner's name" differently.
        foreach (var arg in OperatingSystem.IsLinux() ? new[] { "-c", "%U", path } : new[] { "-f", "%Su", path })
            start.ArgumentList.Add(arg);
        try
        {
            using var stat = Process.Start(start);
            if (stat is null)
                return null;
            var output = stat.StandardOutput.ReadToEndAsync();
            if (!stat.WaitForExit(5000))
            {
                stat.Kill();
                return null;
            }
            var name = output.GetAwaiter().GetResult().Trim();
            return stat.ExitCode == 0 && name.Length > 0 && name != "UNKNOWN" ? name : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
