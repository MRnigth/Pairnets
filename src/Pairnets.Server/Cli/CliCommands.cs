using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Cli;

/// <summary>
/// Maintenance commands: "rescan", "history list|restore|purge" require the service to be stopped (the
/// data directory lock enforces this); "devices list|remove" work while it runs (auth.db is shared
/// safely; the service notices a removal within 30 seconds). None of them needs the token.
/// </summary>
public static class CliCommands
{
    public const string Usage = """
        Usage:
          pairnets-server                         run the server (needs Sync:Token / SYNC_TOKEN)
          pairnets-server rescan [--dry-run]      add files placed in files/ by hand, report missing ones
          pairnets-server history list <path>     list stored versions of a file
          pairnets-server history restore <path> <id>
                                                  restore a version as the new current version
          pairnets-server history purge [--dry-run]
                                                  apply the retention policy now
          pairnets-server devices list            the computers with their own key
          pairnets-server devices remove <name or id>
                                                  remove a computer: its key stops working
          pairnets-server owner-link [--if-new]   print a one-time link to set up (or get back into)
                                                  the nest's website; --if-new: only if no way to
                                                  sign in is set up yet
          pairnets-server --version

        Options:
          --data-dir <dir>                        data directory (default: Sync:DataDir or /var/lib/pairnets)

        Stop the service before running rescan and history commands:
          sudo systemctl stop pairnets-server
          sudo -u pairnets /opt/pairnets/pairnets-server rescan --data-dir /var/lib/pairnets
          sudo systemctl start pairnets-server
        The devices and owner-link commands work while it runs (always as the pairnets user):
          sudo -u pairnets /opt/pairnets/pairnets-server devices list
        """;

    public static bool IsCliCommand(string[] args) =>
        args.Length > 0 && args[0] is "rescan" or "history" or "devices" or "owner-link" or "--help" or "-h" or "help" or "--version";

    /// <param name="whoOwns">
    /// Tells who owns the data folder and who runs the command (<see cref="DataDirUser.Probe"/> unless a test
    /// passes its own). A command run as anyone but the owner stops before it touches a file.
    /// </param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, DataDirUser.Users?>? whoOwns = null)
    {
        if (args[0] is "--help" or "-h" or "help")
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        if (args[0] == "--version")
        {
            await output.WriteLineAsync(typeof(CliCommands).Assembly.GetName().Version?.ToString(3) ?? "unknown");
            return 0;
        }

        var positional = new List<string>();
        var dryRun = false;
        var ifNew = false;
        string? dataDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--if-new":
                    ifNew = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = SyncOptions.FromConfiguration(configuration);
        if (dataDir is not null)
            options.DataDir = dataDir;
        var problem = options.ValidateCommon();
        if (problem is not null)
        {
            await error.WriteLineAsync(problem);
            return 2;
        }

        // auth.db and manifest.db belong to the service's user: anyone else (root included) is sent there.
        var users = (whoOwns ?? DataDirUser.Probe)(options.DataDir);
        if (DataDirUser.Problem(users, options.DataDir, args) is { } wrongUser)
        {
            await error.WriteLineAsync(wrongUser);
            return 1;
        }
        try
        {
            return await RunCommandAsync(options, positional, dryRun, ifNew, output, error);
        }
        catch (Exception ex) when (!OperatingSystem.IsWindows() && DataDirUser.IsAccessDenied(ex))
        {
            await error.WriteLineAsync(DataDirUser.AccessDenied(users, options.DataDir, args));
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(SyncOptions options, List<string> positional, bool dryRun, bool ifNew, TextWriter output, TextWriter error)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var paths = new ServerPaths(options.DataDir);
        if (positional is ["devices", ..])
            return await DevicesAsync(paths, positional, output, error);
        if (positional is ["owner-link"])
            return await OwnerLinkAsync(paths, ifNew, output, error);
        FileStream dataLock;
        try
        {
            paths.EnsureCreated();
            dataLock = SyncStore.AcquireDataDirLock(paths);
        }
        catch (Exception ex) when (ex is IOException || (ex is UnauthorizedAccessException && OperatingSystem.IsWindows()))
        {
            await error.WriteLineAsync(ex.Message);
            return 3;
        }

        using (dataLock)
        using (var store = new SyncStore(paths, loggerFactory.CreateLogger<SyncStore>()))
        {
            store.Initialize(takeLock: false);
            try
            {
                return positional switch
                {
                    ["rescan"] => await RescanAsync(store, dryRun, output),
                    ["history", "list", var path] => await HistoryListAsync(store, path, output, error),
                    ["history", "restore", var path, var id] => await HistoryRestoreAsync(store, path, id, output, error),
                    ["history", "purge"] => await HistoryPurgeAsync(store, options, dryRun, output),
                    _ => await UsageErrorAsync(error),
                };
            }
            finally
            {
                ManifestStore.ReleasePools();
            }
        }
    }

    private static async Task<int> DevicesAsync(ServerPaths paths, List<string> positional, TextWriter output, TextWriter error)
    {
        if (!File.Exists(paths.AuthDatabase))
        {
            await output.WriteLineAsync("No computer has its own key yet.");
            return 0;
        }
        var auth = new AuthStore(paths);
        try
        {
            switch (positional)
            {
                case ["devices", "list"]:
                    var all = auth.ListDevices();
                    if (all.Count == 0)
                        await output.WriteLineAsync("No computer has its own key yet.");
                    foreach (var d in all)
                        await output.WriteLineAsync($"{d.Id}  {d.Name,-24} {d.System ?? "-",-10} added {d.Created:yyyy-MM-dd} ({d.ApprovedBy})");
                    await output.WriteLineAsync($"Shared token: {(auth.AllowSharedToken ? "still accepted" : "turned off")}");
                    return 0;
                case ["devices", "remove", var which]:
                    var device = auth.GetDevice(which) is { IsActive: true } byId ? byId : auth.FindActiveByName(which);
                    if (device is null)
                    {
                        await error.WriteLineAsync($"No computer called '{which}'. See: pairnets-server devices list");
                        return 1;
                    }
                    auth.RemoveDevice(device.Id);
                    await output.WriteLineAsync($"Removed {device.Name}. Its key stops working within 30 seconds; its files stay on it.");
                    return 0;
                default:
                    return await UsageErrorAsync(error);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Prints a link that signs a browser in to the nest's website once, within 24 hours: for the first visit,
    /// and to get back in after losing every way to sign in. The code is in the part after "#", which browsers
    /// never send to a server, so it cannot end up in any log.
    /// </summary>
    private static async Task<int> OwnerLinkAsync(ServerPaths paths, bool ifNew, TextWriter output, TextWriter error)
    {
        if (!Directory.Exists(paths.DataDir))
        {
            await error.WriteLineAsync($"No data folder at {paths.DataDir}. Is the server installed?");
            return 1;
        }
        var auth = new AuthStore(paths);
        try
        {
            if (ifNew && auth.HasSignInMethod)
                return 0;
            if (auth.GetSetting(AuthStore.SettingPublicUrl) is not { Length: > 0 } url)
            {
                await error.WriteLineAsync("This nest has no website yet: give it its own name first with: sudo ./install.sh --public-url https://nest.example.com");
                return 1;
            }
            await output.WriteLineAsync($"{url}/setup#code={auth.CreateSetupCode()}");
            return 0;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<int> UsageErrorAsync(TextWriter error)
    {
        await error.WriteLineAsync(Usage);
        return 2;
    }

    public static async Task<int> RescanAsync(SyncStore store, bool dryRun, TextWriter output)
    {
        var drift = store.DetectDrift();
        var added = 0;
        var updated = 0;
        foreach (var path in drift.Unknown.Concat(drift.SizeMismatch))
        {
            var full = store.ResolveFilesPath(path);
            if (full is null)
                continue;
            var info = new FileInfo(full);
            var (hash, size) = await ContentHash.OfFileAsync(full);
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            var isNew = drift.Unknown.Contains(path);
            if (dryRun)
            {
                await output.WriteLineAsync($"{(isNew ? "would add" : "would update")}: {path}");
                continue;
            }
            var result = await store.AdoptExistingFileAsync(path, hash, size, mtime);
            switch (result.Status)
            {
                case ChangeStatus.Ok:
                    if (isNew)
                        added++;
                    else
                        updated++;
                    await output.WriteLineAsync($"{(isNew ? "added" : "updated")}: {path} (version {result.Entry!.Version})");
                    break;
                case ChangeStatus.Unchanged:
                    break;
                default:
                    await output.WriteLineAsync($"skipped: {path}: {result.Message}");
                    break;
            }
        }

        // Files whose content changed by hand without a size change are found by hashing.
        if (!dryRun)
        {
            foreach (var entry in store.Manifest.AllLive())
            {
                if (drift.Missing.Contains(entry.Path) || drift.SizeMismatch.Contains(entry.Path))
                    continue;
                var full = store.ResolveFilesPath(entry.Path);
                if (full is null || !File.Exists(full))
                    continue;
                var (hash, size) = await ContentHash.OfFileAsync(full);
                if (hash == entry.Hash)
                    continue;
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
                var result = await store.AdoptExistingFileAsync(entry.Path, hash, size, mtime);
                if (result.Status == ChangeStatus.Ok)
                {
                    updated++;
                    await output.WriteLineAsync($"updated: {entry.Path} (content changed by hand)");
                }
            }
        }

        foreach (var path in drift.Missing)
            await output.WriteLineAsync($"missing: {path} (listed in the manifest but not in files/; restore it from history or a backup)");
        foreach (var path in drift.InvalidNames)
            await output.WriteLineAsync($"invalid name, ignored: {path}");
        foreach (var path in drift.Symlinks)
            await output.WriteLineAsync($"symlink, ignored: {path}");
        await output.WriteLineAsync($"rescan {(dryRun ? "(dry run) " : string.Empty)}done: {added} added, {updated} updated, {drift.Missing.Count} missing");
        return 0;
    }

    private static async Task<int> HistoryListAsync(SyncStore store, string path, TextWriter output, TextWriter error)
    {
        if (!PathRules.IsValid(path))
        {
            await error.WriteLineAsync("Invalid path.");
            return 2;
        }
        var versions = store.ListHistory(path);
        if (versions.Count == 0)
            await output.WriteLineAsync("No stored versions.");
        foreach (var v in versions)
            await output.WriteLineAsync($"{v.Id}  {v.StoredAtUtc:yyyy-MM-dd HH:mm:ss}Z  {v.Size,12} bytes");
        return 0;
    }

    private static async Task<int> HistoryRestoreAsync(SyncStore store, string path, string id, TextWriter output, TextWriter error)
    {
        var result = await store.RestoreAsync(path, id, CancellationToken.None);
        switch (result.Status)
        {
            case ChangeStatus.Ok:
                await output.WriteLineAsync($"Restored {path} from {id} (version {result.Entry!.Version}). Clients pick it up on their next sync.");
                return 0;
            case ChangeStatus.Unchanged:
                await output.WriteLineAsync("The current version already has this content.");
                return 0;
            default:
                await error.WriteLineAsync($"Restore failed: {result.Status} {result.Message}");
                return 1;
        }
    }

    private static async Task<int> HistoryPurgeAsync(SyncStore store, SyncOptions options, bool dryRun, TextWriter output)
    {
        var (count, bytes) = await store.PurgeHistoryAsync(TimeSpan.FromDays(options.HistoryRetentionDays), options.HistoryMinVersions, dryRun, CancellationToken.None);
        await output.WriteLineAsync($"{(dryRun ? "Would delete" : "Deleted")} {count} version(s), {bytes} bytes (retention {options.HistoryRetentionDays} days, keeping at least {options.HistoryMinVersions} per file).");
        return 0;
    }
}
