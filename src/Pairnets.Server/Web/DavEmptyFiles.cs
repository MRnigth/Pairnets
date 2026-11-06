using Microsoft.AspNetCore.SignalR;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// New files a WebDAV client made empty and has not written into yet. Finder and Explorer make a file with an empty PUT, lock it,
/// and then PUT its content. Storing the empty one first would put a throwaway version in history and push it to every computer,
/// and for a file over 25 MB leave an empty file on every computer when the content then gets 413. So an empty PUT where no file
/// is, is held here instead (in memory) and listed, read, locked, renamed and deleted as if it were there:
/// <list type="bullet">
/// <item>the PUT with the content stores the file, as one version;</item>
/// <item>content that is refused (over 25 MB) takes the held file away with it, and so does a DELETE or removing the link;</item>
/// <item>when nothing more comes, the file was meant to be empty: <see cref="DavEmptyFileKeeper"/> stores it as an empty file once
/// nothing has happened to it for <see cref="QuietTime"/>, or for <see cref="LockedTime"/> while a client holds a lock on it (its
/// content is on its way: Finder copies a big file into its own cache first). A stopping nest stores the ones nobody holds a lock on.</item>
/// </list>
/// docs/DECISIONS.md, "WebDAV link".
/// </summary>
public sealed class DavEmptyFiles(TimeProvider? clock = null)
{
    /// <summary>How long a held file nobody holds a lock on waits for its content before it is stored empty.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromMinutes(2);

    /// <summary>How long a held file a client holds a lock on waits: as long as the lock it was given (<c>Second-3600</c>). A LOCK refresh starts it over.</summary>
    public static readonly TimeSpan LockedTime = TimeSpan.FromHours(1);

    /// <summary>The most files held at once. Past it an empty PUT stores its file at once (the way it was before files were held).</summary>
    public const int MaxHeld = 5000;

    /// <summary>A held file's ETag: not a SHA-256, so it never matches a stored version.</summary>
    public const string ETag = "new-empty-file";

    /// <summary>A held file as the keeper stores it: where, when it was made, and the link that made it (named in the push).</summary>
    public sealed record HeldFile(string Path, DateTimeOffset Made, DeviceIdentity By);

    private sealed class Held(DateTimeOffset made, DeviceIdentity by)
    {
        public DateTimeOffset Made { get; } = made;

        public DeviceIdentity By { get; set; } = by;

        public DateTimeOffset Due { get; set; }

        public bool Locked { get; set; }

        /// <summary>Requests writing content into it right now: while there are any it is never stored empty.</summary>
        public int Writers { get; set; }
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Held> _files = new(StringComparer.Ordinal);

    /// <summary>How many files are held now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _files.Count;
        }
    }

    private void Wait(Held held) => held.Due = _clock.GetUtcNow() + (held.Locked ? LockedTime : QuietTime);

    /// <summary>
    /// Holds <paramref name="path"/>, made empty by <paramref name="by"/> (again: a second empty PUT keeps when it was made and
    /// starts the wait over). False when <see cref="MaxHeld"/> files are held already: then the caller stores it at once.
    /// </summary>
    public bool TryAdd(string path, DeviceIdentity by)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var held))
            {
                if (_files.Count >= MaxHeld)
                    return false;
                held = new Held(_clock.GetUtcNow(), by);
                _files[path] = held;
            }
            held.By = by;
            Wait(held);
            return true;
        }
    }

    /// <summary>A LOCK (or its refresh) on a held file: its content is on its way, so it waits up to <see cref="LockedTime"/>.</summary>
    public void Locked(string path) => Update(path, h => h.Locked = true);

    /// <summary>An UNLOCK: the client is done with it. If no content came, it is stored empty after <see cref="QuietTime"/>.</summary>
    public void Unlocked(string path) => Update(path, h => h.Locked = false);

    /// <summary>Starts a held file's wait over (a PROPPATCH on it: the client is still busy with it).</summary>
    public void Touch(string path) => Update(path, _ => { });

    private void Update(string path, Action<Held> change)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(path, out var held))
            {
                change(held);
                Wait(held);
            }
        }
    }

    /// <summary>
    /// A request starts writing content into the file held at <paramref name="path"/>: until the returned handle is disposed it is
    /// never stored empty (an upload may take minutes). Null when nothing is held there.
    /// </summary>
    public IDisposable? Writing(string path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var held))
                return null;
            held.Writers++;
            return new WriteHandle(this, path, held);
        }
    }

    private sealed class WriteHandle(DavEmptyFiles owner, string path, Held held) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_done)
                    return;
                _done = true;
                held.Writers--;
                if (owner._files.TryGetValue(path, out var now) && ReferenceEquals(now, held))
                    owner.Wait(held); // the write ended without storing it (refused, or the client went): it waits again
            }
        }
    }

    /// <summary>When the file held at <paramref name="path"/> was made; null when none is.</summary>
    public DateTimeOffset? MadeAt(string path)
    {
        lock (_gate)
            return _files.TryGetValue(path, out var held) ? held.Made : null;
    }

    /// <summary>Stops holding <paramref name="path"/>; true when it was held.</summary>
    public bool Remove(string path)
    {
        lock (_gate)
            return _files.Remove(path);
    }

    /// <summary>Stops holding every file inside folder <paramref name="folder"/>.</summary>
    public void RemoveUnder(string folder)
    {
        lock (_gate)
        {
            foreach (var key in _files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).ToList())
                _files.Remove(key);
        }
    }

    /// <summary>Stops holding every file the link <paramref name="linkId"/> made: a removed link's files are never stored.</summary>
    public int RemoveBy(string linkId)
    {
        lock (_gate)
        {
            var keys = _files.Where(f => f.Value.By.Id == linkId).Select(f => f.Key).ToList();
            foreach (var key in keys)
                _files.Remove(key);
            return keys.Count;
        }
    }

    /// <summary>The held files inside folder <paramref name="path"/> ("" for all), with when each was made.</summary>
    public IReadOnlyList<(string Path, DateTimeOffset Made)> Under(string path)
    {
        var prefix = path.Length == 0 ? string.Empty : path + "/";
        lock (_gate)
        {
            return _files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => (f.Key, f.Value.Made)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>A rename (MOVE): the file held at <paramref name="from"/>, or the held files inside folder <paramref name="from"/>, are held under <paramref name="to"/>.</summary>
    public void Move(string from, string to)
    {
        lock (_gate)
        {
            var moving = _files.Where(f => f.Key == from || f.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList();
            foreach (var (key, _) in moving)
                _files.Remove(key);
            foreach (var (key, held) in moving)
            {
                Wait(held);
                _files[to + key[from.Length..]] = held;
            }
        }
    }

    /// <summary>The held files nothing more came for (no request writing into them, their wait over), no longer held: the keeper stores them.</summary>
    public IReadOnlyList<HeldFile> TakeDue()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
            return Take(h => h.Writers == 0 && h.Due <= now);
    }

    /// <summary>
    /// When the nest stops: every held file goes. Returned (for the keeper to store) are the ones nobody holds a lock on or is
    /// writing into: a locked one's content was on its way, and the client puts it again after the restart.
    /// </summary>
    public IReadOnlyList<HeldFile> TakeAllForStop()
    {
        lock (_gate)
        {
            var keep = Take(h => h.Writers == 0 && !h.Locked);
            _files.Clear();
            return keep;
        }
    }

    private List<HeldFile> Take(Func<Held, bool> which)
    {
        var taken = _files.Where(f => which(f.Value)).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new HeldFile(f.Key, f.Value.Made, f.Value.By)).ToList();
        foreach (var file in taken)
            _files.Remove(file.Path);
        return taken;
    }
}

/// <summary>
/// Stores the empty files <see cref="DavEmptyFiles"/> holds once nothing more comes for them: such a file was meant to be empty
/// (an empty file copied in, a "New Text Document" left as it is). Every <see cref="Interval"/>, and once more when the nest stops.
/// A file is stored as a WebDAV PUT would have stored it (base "none", made when the client made it) and announced in the link's
/// name. It is not stored when its link was removed meanwhile, the nest is encrypted now, or a file of that name was stored meanwhile.
/// </summary>
public sealed class DavEmptyFileKeeper(DavEmptyFiles held, SyncStore store, AuthStore auth, IHubContext<SyncHub> hub, IServiceProvider services,
    ILogger<DavEmptyFileKeeper> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await StoreDueAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: StopAsync stores what is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await StoreAsync(held.TakeAllForStop());
    }

    /// <summary>Stores the held files whose wait is over; how many were stored.</summary>
    public Task<int> StoreDueAsync() => StoreAsync(held.TakeDue());

    private async Task<int> StoreAsync(IReadOnlyList<DavEmptyFiles.HeldFile> files)
    {
        var stored = 0;
        foreach (var file in files)
        {
            try
            {
                if (await StoreOneAsync(file))
                    stored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogWarning("Could not store the empty file {Path} a WebDAV link made: {Error}", file.Path, ex.Message);
            }
        }
        return stored;
    }

    private async Task<bool> StoreOneAsync(DavEmptyFiles.HeldFile file)
    {
        if (store.Sealed)
            return false;
        if (file.By.Id is { } id && auth.GetDevice(id) is not { IsActive: true })
        {
            log.LogInformation("Dropped the empty file {Path}: the WebDAV link that made it was removed", file.Path);
            return false;
        }
        if (store.Manifest.Get(file.Path) is { Deleted: false })
            return false; // stored meanwhile (with its content, or by an app): nothing to add
        var result = await store.PutAsync(file.Path, "none", file.Made.ToUnixTimeMilliseconds(), Stream.Null, CancellationToken.None);
        if (result.Status != ChangeStatus.Ok)
        {
            log.LogInformation("Did not store the empty file {Path} a WebDAV link made: {Status} {Message}", file.Path, result.Status, result.Message);
            return false;
        }
        await Endpoints.BroadcastAsync(services, hub, file.By.Name, file.By.RegistryKey, file.Path);
        log.LogInformation("Stored the empty file {Path}: nothing was written into it", file.Path);
        return true;
    }
}
