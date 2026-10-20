using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// Files opened from the Files page while their content is on the nest only: each is a copy in this computer's own app
/// folder (readable by this user only), watched, and saved back to the nest when it is changed and saved, with the
/// version it was opened as for base (the nest keeps the one it replaces in History). When the nest changed it
/// meanwhile, the change is kept as a conflict copy next to it, as the sync does. A copy is removed a day after its
/// last change reached the nest, never before.
/// </summary>
public sealed class OpenedFiles : IDisposable
{
    /// <summary>A copy that reached the nest is removed this long after it was last changed.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    /// <summary>A change is saved back once the file has been quiet this long (programs write in several steps).</summary>
    internal TimeSpan QuietFor { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ClientSession _session;
    private readonly string _dir;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Copy> _copies = new(StringComparer.Ordinal); // by id
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private Timer? _timer;

    /// <summary>One opened copy: where it is, which file on the nest it is, and the nest's version it now matches.</summary>
    public sealed record Copy(string Id, string Path, string Name, string Base, long SavedMs);

    internal OpenedFiles(ClientSession session, string dir, ILogger log, TimeProvider clock)
    {
        _session = session;
        _dir = dir;
        _log = log;
        _clock = clock;
        foreach (var copy in Load())
        {
            _copies[copy.Id] = copy;
            Watch(copy);
        }
        _timer = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
    }

    /// <summary>Raised when a change could not be saved to the nest because it is full (the app shows a pop-up).</summary>
    public event Action<string>? NestFullWhileSaving;

    /// <summary>Raised when a change was saved to the nest, with the file's path there.</summary>
    public event Action<string>? Saved;

    public IReadOnlyList<Copy> Copies
    {
        get
        {
            lock (_gate)
                return [.. _copies.Values];
        }
    }

    private string IndexPath => Path.Combine(_dir, "opened.json");

    /// <summary>Downloads version <paramref name="hash"/> of <paramref name="path"/> into a new copy and returns its full path.</summary>
    internal async Task<string> OpenAsync(string path, string hash, CancellationToken ct)
    {
        lock (_gate)
        {
            // Already open, unchanged since and still that version: the same copy.
            var open = _copies.Values.FirstOrDefault(c => c.Path == path && c.Base == hash);
            if (open is not null && File.Exists(Full(open)))
                return Full(open);
        }
        var copy = new Copy(Guid.NewGuid().ToString("N")[..12], path, PathRules.FileName(path), hash, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        var full = Full(copy);
        CreatePrivateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            await using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await _session.FetchVersionAsync(path, hash, output, _ => { }, ct).ConfigureAwait(false);
            if (_session.State.GetRemote(path) is { ModifiedMs: > 0 } entry)
                File.SetLastWriteTimeUtc(full, DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedMs).UtcDateTime);
        }
        catch
        {
            TryDeleteDirectory(Path.GetDirectoryName(full)!);
            throw;
        }
        lock (_gate)
            _copies[copy.Id] = copy;
        Save();
        Watch(copy);
        return full;
    }

    private string Full(Copy copy) => Path.Combine(_dir, copy.Id, copy.Name);

    private void Watch(Copy copy)
    {
        var folder = Path.Combine(_dir, copy.Id);
        if (!Directory.Exists(folder))
            return;
        var watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // Programs often save by writing a new file and renaming it over the old one: any change in the folder counts.
        watcher.Changed += (_, _) => Later(copy.Id);
        watcher.Created += (_, _) => Later(copy.Id);
        watcher.Renamed += (_, _) => Later(copy.Id);
        watcher.EnableRaisingEvents = true;
        lock (_gate)
        {
            if (_watchers.Remove(copy.Id, out var old))
                old.Dispose();
            _watchers[copy.Id] = watcher;
        }
    }

    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    private void Later(string id)
    {
        CancellationTokenSource wait;
        lock (_gate)
        {
            if (_pending.Remove(id, out var old))
                old.Cancel();
            _pending[id] = wait = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(QuietFor, _clock, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await SaveBackAsync(id, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Saves one copy back to the nest when its content differs from the version it matches. True when the nest has it
    /// now (or nothing changed); false when it must be tried again (in use, nest offline or full).
    /// </summary>
    internal async Task<bool> SaveBackAsync(string id, CancellationToken ct)
    {
        Copy? copy;
        lock (_gate)
            _copies.TryGetValue(id, out copy);
        if (copy is null || !File.Exists(Full(copy)))
            return true;
        await _saving.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
                copy = _copies.GetValueOrDefault(id);
            if (copy is null)
                return true;
            var full = Full(copy);
            FileStream stream;
            try
            {
                stream = ContentHash.OpenForSharedRead(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // still being written: the next change or sweep tries again
            }
            await using (stream)
            {
                var hash = await ContentHash.OfStreamAsync(stream, ct).ConfigureAwait(false);
                if (hash == copy.Base)
                    return true;
                if (_session.Status.ServerSpaceLow)
                {
                    NestFullWhileSaving?.Invoke(copy.Path);
                    return false;
                }
                stream.Position = 0;
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
                ApiResult result;
                try
                {
                    (result, _, _) = await _session.Api.UploadAsync(copy.Path, copy.Base, mtime, stream, null, ct).ConfigureAwait(false);
                    if (result.Outcome == ApiOutcome.Conflict)
                    {
                        // Changed on the nest since it was opened: this change is kept beside it, as the sync does.
                        var name = ConflictNames.Make(copy.Path, _session.Settings.DeviceName ?? "this PC", _clock.GetLocalNow().DateTime,
                            p => _session.State.GetRemote(p) is { Deleted: false });
                        stream.Position = 0;
                        (result, _, _) = await _session.Api.UploadAsync(name, ContentHash.NoneBase, mtime, stream, null, ct).ConfigureAwait(false);
                        if (result.Outcome == ApiOutcome.Ok)
                        {
                            _session.Activity.Add(ActivityKind.Conflict, copy.Path,
                                $"{copy.Name} changed on your nest while it was open here; your version was kept as \"{PathRules.FileName(name)}\"", _clock);
                            copy = copy with { Path = name };
                        }
                    }
                }
                catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
                {
                    _log.LogInformation("Saving {Path} to the nest waits: {Error}", copy.Path, ex.Message);
                    return false;
                }
                if (result.Outcome != ApiOutcome.Ok)
                {
                    _log.LogWarning("The nest did not take the change to {Path}: {Outcome} {Message}", copy.Path, result.Outcome, result.Message);
                    return false;
                }
                _session.State.UpsertRemote(result.Entry!);
                var saved = copy with { Base = result.Entry!.Hash!, SavedMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() };
                lock (_gate)
                    _copies[id] = saved;
                Save();
                _session.Runner.RequestSync("a file opened from the nest was saved");
                _log.LogInformation("Saved {Path} back to the nest", saved.Path);
                Saved?.Invoke(saved.Path);
                return true;
            }
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Saves what is still waiting (offline or full before) and removes copies whose last change reached the nest over
    /// <see cref="KeepFor"/> ago. A copy with a change the nest doesn't have yet is never removed.
    /// </summary>
    internal async Task SweepAsync()
    {
        foreach (var copy in Copies)
        {
            try
            {
                if (!await SaveBackAsync(copy.Id, CancellationToken.None).ConfigureAwait(false))
                    continue;
                var full = Full(copy);
                var quietSince = File.Exists(full) ? new DateTimeOffset(File.GetLastWriteTimeUtc(full)) : DateTimeOffset.MinValue;
                var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(copy.SavedMs);
                var last = quietSince > savedAt ? quietSince : savedAt;
                if (_clock.GetUtcNow() - last < KeepFor)
                    continue;
                lock (_gate)
                {
                    _copies.Remove(copy.Id);
                    if (_watchers.Remove(copy.Id, out var watcher))
                        watcher.Dispose();
                }
                TryDeleteDirectory(Path.Combine(_dir, copy.Id));
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log.LogDebug("Opened copy {Id} is looked at again later: {Error}", copy.Id, ex.Message);
            }
        }
    }

    private List<Copy> Load()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<List<Copy>>(File.ReadAllText(IndexPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The list of files opened from the nest could not be read: {Error}", ex.Message);
            return [];
        }
    }

    private void Save()
    {
        List<Copy> copies;
        lock (_gate)
            copies = [.. _copies.Values];
        try
        {
            CreatePrivateDirectory(_dir);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copies));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The list of files opened from the nest could not be saved: {Error}", ex.Message);
        }
    }

    /// <summary>A folder only this user can read (on Windows the app's own folder already is).</summary>
    private static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next sweep.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            foreach (var w in _watchers.Values)
                w.Dispose();
            _watchers.Clear();
            foreach (var p in _pending.Values)
                p.Cancel();
        }
    }
}
