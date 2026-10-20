using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Infrastructure;

/// <summary>A simulated PC: its own sync folder, state database, API client and engine.</summary>
public sealed class Device : IAsyncDisposable
{
    private readonly TempDir _folder;
    private readonly TempDir _stateDir;
    private readonly Func<StateDb, PairnetsApiClient, SyncEngine> _makeEngine;
    private readonly Func<PairnetsApiClient> _makeApi;

    /// <param name="requests">Records every request this computer's API client sends (also after <see cref="RestartAppAsync"/>).</param>
    /// <param name="configureApi">Applied to the API client (also after a restart), e.g. shorter retries.</param>
    /// <param name="resumeDownloadsFrom">Downloads of files at least this big continue where they stopped (default: the app's).</param>
    public Device(string name, TestServer server, Uri? url = null, string? token = null, TimeSpan? stability = null,
        IReadOnlyList<string>? extraIgnore = null, TimeProvider? clock = null, int parallel = 4, long? pieceSize = null,
        RequestLog? requests = null, Action<PairnetsApiClient>? configureApi = null, long? resumeDownloadsFrom = null)
    {
        Name = name;
        Server = server;
        _folder = new TempDir(name);
        _stateDir = new TempDir(name + "-state");
        Url = url ?? server.Url;
        Token = token ?? server.Token;
        _makeApi = () =>
        {
            var api = new PairnetsApiClient(Url, Token, name, requests?.Handler(), stallTimeout: TimeSpan.FromSeconds(20), pieceSize: pieceSize);
            configureApi?.Invoke(api);
            return api;
        };
        _makeEngine = (state, api) => new SyncEngine(new EngineOptions
        {
            Folder = _folder.Path,
            DeviceName = name,
            StabilityWindow = stability ?? TimeSpan.Zero,
            ExtraIgnore = extraIgnore ?? [],
            MaxParallelTransfers = parallel,
            ResumeDownloadsFrom = resumeDownloadsFrom ?? new EngineOptions { Folder = ".", DeviceName = "x" }.ResumeDownloadsFrom,
            Placement = _placement,
        }, api, state, clock: clock, hooks: Hooks) { NestFull = () => NestFull };
        State = new StateDb(StatePath);
        Api = _makeApi();
        Engine = _makeEngine(State, Api);
    }

    public string Name { get; }
    public TestServer Server { get; }
    public Uri Url { get; }
    public string Token { get; }
    public string Folder => _folder.Path;
    public string StatePath => Path.Combine(_stateDir.Path, "state.db");
    public StateDb State { get; private set; }
    public PairnetsApiClient Api { get; private set; }
    public SyncEngine Engine { get; private set; }

    /// <summary>
    /// Closes and starts the app again: the runner stops, and a new state.db connection, API client and engine
    /// are made for the same folder and state (nothing is kept in memory).
    /// </summary>
    public async Task RestartAppAsync()
    {
        if (Runner is not null)
        {
            await Runner.DisposeAsync();
            Runner = null;
        }
        Api.Dispose();
        State.Dispose();
        State = new StateDb(StatePath);
        Api = _makeApi();
        Engine = _makeEngine(State, Api);
    }
    public EngineHooks Hooks { get; } = new();

    private PlacementMap _placement = PlacementMap.Everywhere;

    /// <summary>Where each folder's files live on this computer (also after <see cref="RestartAppAsync"/>).</summary>
    public PlacementMap Placement
    {
        get => _placement;
        set
        {
            _placement = value;
            Engine.Placement = value;
        }
    }

    /// <summary>Sets "Where your files live" for folders, e.g. <c>Place(("Photos", FilePlacement.NestOnly))</c>.</summary>
    public void Place(params (string Folder, FilePlacement Placement)[] folders) =>
        Placement = new PlacementMap(folders.Select(f => KeyValuePair.Create(f.Folder, f.Placement)));

    /// <summary>The nest counts as (nearly) full for this computer's engine.</summary>
    public bool NestFull { get; set; }
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

    /// <summary>All synced-looking files (relative, sorted), excluding Pairnets bookkeeping.</summary>
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
