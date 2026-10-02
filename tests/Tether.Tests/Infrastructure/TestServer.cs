using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tether.Core.Api;
using Tether.Server;
using Tether.Server.Storage;

namespace Tether.Tests.Infrastructure;

/// <summary>The real Tether server, in-process, on a random localhost port with a temp data dir.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly TempDir? _ownedDir;
    private readonly Dictionary<string, string?> _extraConfig;
    private WebApplication? _app;

    private TestServer(string dataDir, string token, TempDir? ownedDir, Dictionary<string, string?> extraConfig)
    {
        DataDir = dataDir;
        Token = token;
        _ownedDir = ownedDir;
        _extraConfig = extraConfig;
    }

    public string DataDir { get; }

    public string Token { get; }

    public Uri Url { get; private set; } = null!;

    public int Port => Url.Port;

    public SyncStore Store => _app!.Services.GetRequiredService<SyncStore>();

    public ServerPaths Paths => new(DataDir);

    /// <summary>Log lines captured from the server (for "never logs the token" checks).</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public static async Task<TestServer> StartAsync(string? token = null, Dictionary<string, string?>? config = null)
    {
        var dir = new TempDir("srv");
        var server = new TestServer(dir.Path, token ?? "test-token-" + Guid.NewGuid().ToString("N"), dir, config ?? []);
        await server.StartOnPortAsync(0);
        return server;
    }

    private async Task StartOnPortAsync(int port)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Sync:Token"] = Token,
            ["Sync:DataDir"] = DataDir,
            ["Sync:PurgeInitialDelay"] = "01:00:00",
        };
        foreach (var (k, v) in _extraConfig)
            settings[k] = v;

        _app = TetherServerHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddProvider(Logs);
        });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Url = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal) + "/");
    }

    /// <summary>Stops the server (in-flight requests are completed or aborted).</summary>
    public async Task StopAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        ManifestStore.ReleasePools();
    }

    /// <summary>Restarts on the same port and data directory (simulates a service restart).</summary>
    public async Task RestartAsync()
    {
        var port = Port;
        await StopAsync();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await StartOnPortAsync(port);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }

    public TetherApiClient Client(string device = "test", string? token = null, Uri? url = null) =>
        new(url ?? Url, token ?? Token, device);

    public HttpClient RawHttp(string? token = null)
    {
        var http = new HttpClient { BaseAddress = Url };
        if (token is not null)
            http.DefaultRequestHeaders.Add("X-Sync-Token", token);
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ownedDir?.Dispose();
    }
}

/// <summary>Collects every formatted log line.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
                owner._lines.Add($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
