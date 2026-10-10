using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server;
using Pairnets.Server.Storage;

namespace Pairnets.Tests.Infrastructure;

/// <summary>The real Pairnets server, in-process, on a random localhost port with a temp data dir.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly TempDir? _ownedDir;
    private readonly Dictionary<string, string?> _extraConfig;
    private WebApplication? _app;

    private readonly bool _https;
    private int _httpsPort;
    private Action<WebApplicationBuilder>? _configureBuilder;

    private TestServer(string dataDir, string token, TempDir? ownedDir, Dictionary<string, string?> extraConfig, bool https)
    {
        DataDir = dataDir;
        Token = token;
        _ownedDir = ownedDir;
        _extraConfig = extraConfig;
        _https = https;
    }

    public string DataDir { get; }

    public string Token { get; }

    public Uri Url { get; private set; } = null!;

    /// <summary>The HTTPS address when started with <c>https: true</c>.</summary>
    public Uri? HttpsUrl { get; private set; }

    /// <summary>Where the server reads fullchain.pem and privkey.pem (DataDir/tls unless Sync:TlsDir is set).</summary>
    public string TlsDir => _extraConfig.TryGetValue("Sync:TlsDir", out var dir) && dir is not null ? dir : Path.Combine(DataDir, "tls");

    public int Port => Url.Port;

    public SyncStore Store => _app!.Services.GetRequiredService<SyncStore>();

    public UploadSessions Uploads => _app!.Services.GetRequiredService<UploadSessions>();

    /// <summary>The running server's services (auth store, endpoints, ...).</summary>
    public IServiceProvider Services => _app!.Services;

    public ServerPaths Paths => new(DataDir);

    /// <summary>Log lines captured from the server (for "never logs the token" checks).</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <param name="https">
    /// Also listen for HTTPS on another random port. Unless <paramref name="installCertificate"/> is false, a test
    /// certificate for "localhost" is put in <see cref="TlsDir"/> first.
    /// </param>
    public static async Task<TestServer> StartAsync(string? token = null, Dictionary<string, string?>? config = null,
        bool https = false, bool installCertificate = true, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var dir = new TempDir("srv");
        var server = new TestServer(dir.Path, token ?? "test-token-" + Guid.NewGuid().ToString("N"), dir, config ?? [], https)
        {
            _configureBuilder = configureBuilder,
        };
        if (https && installCertificate)
        {
            // Windows builds the chain through the test intermediate and some machines refuse it (see WriteLeafOnlyTo);
            // the full chain is still served on Linux and macOS, like a real Let's Encrypt certificate.
            var issued = TestCertificates.Create("localhost");
            if (OperatingSystem.IsWindows())
                issued.WriteLeafOnlyTo(server.TlsDir);
            else
                issued.WriteTo(server.TlsDir);
        }
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
            ["Sync:UploadStallTimeout"] = "00:00:02",
        };
        if (_https)
            settings["Sync:HttpsUrl"] = $"https://127.0.0.1:{_httpsPort}";
        foreach (var (k, v) in _extraConfig)
            settings[k] = v;

        _app = PairnetsServerHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddProvider(Logs);
            _configureBuilder?.Invoke(builder);
        });
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Url = new Uri(addresses.First(a => a.StartsWith("http:", StringComparison.Ordinal)).Replace("[::]", "127.0.0.1", StringComparison.Ordinal) + "/");
        var https = addresses.FirstOrDefault(a => a.StartsWith("https:", StringComparison.Ordinal));
        HttpsUrl = https is null ? null : new Uri(https + "/");
        _httpsPort = HttpsUrl?.Port ?? 0;
    }

    /// <summary>
    /// A browser for the nest's website: HTTPS on "localhost" (start the server with <c>https: true</c> and
    /// Sync:PublicUrl = https://localhost), cookies kept, and an Origin header like the site's own pages send.
    /// </summary>
    public HttpClient WebBrowser(System.Net.CookieContainer? cookies = null, string? origin = "self")
    {
        var handler = new SocketsHttpHandler { UseProxy = false, CookieContainer = cookies ?? new System.Net.CookieContainer(), AllowAutoRedirect = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var site = new Uri($"https://localhost:{HttpsUrl!.Port}/");
        var http = new HttpClient(handler) { BaseAddress = site };
        if (origin is not null)
            http.DefaultRequestHeaders.Add("Origin", origin == "self" ? site.GetLeftPart(UriPartial.Authority) : origin);
        return http;
    }

    /// <summary>Starts a server whose website runs (HTTPS, public name "localhost").</summary>
    public static Task<TestServer> StartWithWebsiteAsync(Dictionary<string, string?>? config = null, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var all = new Dictionary<string, string?>(config ?? []) { ["Sync:PublicUrl"] = "https://localhost" };
        return StartAsync(config: all, https: true, configureBuilder: configureBuilder);
    }

    /// <summary>
    /// An HttpClient for <see cref="HttpsUrl"/> that trusts whatever certificate the server shows and records it
    /// (a new handler each time, so every client does its own TLS handshake).
    /// </summary>
    public HttpClient HttpsClient(Action<System.Security.Cryptography.X509Certificates.X509Certificate2, System.Security.Cryptography.X509Certificates.X509Chain?>? seen = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, _) =>
        {
            if (cert is not null)
                seen?.Invoke(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert), chain);
            return true;
        };
        return new HttpClient(handler) { BaseAddress = HttpsUrl ?? throw new InvalidOperationException("Started without https") };
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

    public PairnetsApiClient Client(string device = "test", string? token = null, Uri? url = null) =>
        new(url ?? Url, token ?? Token, device);

    /// <summary>
    /// A computer's own key, as Allow on the nest would mint it, written straight into the store and
    /// the device list: no endpoint hands out keys any more, so tests that just need one start here.
    /// </summary>
    public DeviceKeyGrant MintKey(string name)
    {
        var (device, key) = Services.GetRequiredService<Pairnets.Server.Auth.DeviceKeys>().Store.AddDevice(name, "test", "test");
        var identity = new Pairnets.Server.Web.DeviceIdentity(device.Id, device.Name, Pairnets.Server.Web.DeviceAuthKind.DeviceKey);
        Services.GetRequiredService<Pairnets.Server.Services.DeviceRegistry>().Seen(identity.RegistryKey, device.Name, null);
        return new DeviceKeyGrant(device.Id, device.Name, key);
    }

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
