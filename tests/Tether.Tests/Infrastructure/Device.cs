using Tether.Core.Api;
using Tether.Core.Hashing;
using Tether.Core.Paths;
using Tether.Core.State;
using Tether.Core.Sync;

namespace Tether.Tests.Infrastructure;

/// <summary>A simulated PC: its own sync folder, state database, API client and engine.</summary>
public sealed class Device : IAsyncDisposable
{
    private readonly TempDir _folder;
    private readonly TempDir _stateDir;

    public Device(string name, TestServer server, Uri? url = null, string? token = null, TimeSpan? stability = null,
        IReadOnlyList<string>? extraIgnore = null, TimeProvider? clock = null, int parallel = 4, long? pieceSize = null)
    {
        Name = name;
        Server = server;
        _folder = new TempDir(name);
        _stateDir = new TempDir(name + "-state");
        State = new StateDb(Path.Combine(_stateDir.Path, "state.db"));
        Url = url ?? server.Url;
        Token = token ?? server.Token;
        Api = new TetherApiClient(Url, Token, name, stallTimeout: TimeSpan.FromSeconds(20), pieceSize: pieceSize);
        Engine = new SyncEngine(new EngineOptions
        {
            Folder = _folder.Path,
            DeviceName = name,
            StabilityWindow = stability ?? TimeSpan.Zero,
            ExtraIgnore = extraIgnore ?? [],
            MaxParallelTransfers = parallel,
        }, Api, State, clock: clock, hooks: Hooks);
    }

    public string Name { get; }
    public TestServer Server { get; }
    public Uri Url { get; }
    public string Token { get; }
    public string Folder => _folder.Path;
    public StateDb State { get; }
    public TetherApiClient Api { get; }
    public SyncEngine Engine { get; }
    public EngineHooks Hooks { get; } = new();
    public SyncRunner? Runner { get; private set; }

    public Task<PassResult> SyncAsync(bool full = false) => Engine.RunPassAsync(new PassOptions("test", full), CancellationToken.None);

    /// <summary>Runs passes until one changes nothing (and is not waiting for retries).</summary>
    public async Task<PassResult> SyncUntilQuietAsync(int maxPasses = 10)
    {
        for (var i = 0; i < maxPasses; i++)
        {
            var r = await SyncAsync();
            if (r.Outcome != PassOutcome.Completed || (r.Changes == 0 && !r.WantsRetry))
                return r;
        }
        throw new InvalidOperationException($"{Name} did not become quiet after {maxPasses} passes");
    }

    public string Full(string rel) => Path.Combine(Folder, PathRules.ToOsRelative(rel));

    public void Write(string rel, string content) => WriteBytes(rel, System.Text.Encoding.UTF8.GetBytes(content));

    public void WriteBytes(string rel, byte[] content)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public string Read(string rel) => File.ReadAllText(Full(rel));

    public bool Exists(string rel) => File.Exists(Full(rel));

    public void Delete(string rel) => File.Delete(Full(rel));

    /// <summary>All synced-looking files (relative, sorted), excluding Tether bookkeeping.</summary>
    public List<string> Files()
    {
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var rel = PathRules.FromOsRelative(Path.GetRelativePath(Folder, f));
            if (rel == PathRules.MarkerFileName || rel.StartsWith(PathRules.TempFolderName + "/", StringComparison.Ordinal))
                continue;
            list.Add(rel);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>path → content hash for every file.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Files())
            d[f] = ContentHash.Of(File.ReadAllBytes(Full(f)));
        return d;
    }

    public SyncRunner StartRunner(TimeSpan? debounce = null, Func<RunnerOptions, RunnerOptions>? tweak = null)
    {
        var options = new RunnerOptions
        {
            ServerUrl = Url,
            Token = Token,
            DeviceId = Name,
            WatcherDebounce = debounce ?? TimeSpan.FromMilliseconds(300),
            WatcherMaxDelay = TimeSpan.FromSeconds(2),
            RemoteDebounce = TimeSpan.FromMilliseconds(100),
            PeriodicInterval = TimeSpan.FromHours(1),
            UnstableRetry = TimeSpan.FromMilliseconds(500),
            OfflineBackoff = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)],
        };
        Runner = new SyncRunner(Engine, tweak?.Invoke(options) ?? options);
        Runner.Start();
        return Runner;
    }

    public async ValueTask DisposeAsync()
    {
        if (Runner is not null)
            await Runner.DisposeAsync();
        Api.Dispose();
        State.Dispose();
        _folder.Dispose();
        _stateDir.Dispose();
    }
}
