using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Cli;

/// <summary>
/// Maintenance commands: "rescan", "history list|restore|purge". They require the service to be
/// stopped (the data directory lock enforces this) and never need the token.
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
          pairnets-server --version

        Options:
          --data-dir <dir>                        data directory (default: Sync:DataDir or /var/lib/pairnets)

        Stop the service before running maintenance commands:
          sudo systemctl stop pairnets-server
          sudo -u pairnets /opt/pairnets/pairnets-server rescan --data-dir /var/lib/pairnets
          sudo systemctl start pairnets-server
        """;

    public static bool IsCliCommand(string[] args) =>
        args.Length > 0 && args[0] is "rescan" or "history" or "--help" or "-h" or "help" or "--version";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
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
        string? dataDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
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

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var paths = new ServerPaths(options.DataDir);
        FileStream dataLock;
        try
        {
            paths.EnsureCreated();
            dataLock = SyncStore.AcquireDataDirLock(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
