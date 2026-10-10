using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Pairnets.Tests.Infrastructure;

/// <summary>What a run of the server's command line printed, and how it ended.</summary>
public sealed record CliResult(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n{Output}{Error}";
}

/// <summary>
/// The real server program as its own process, set up only through environment variables, like the service:
/// a random token, a temporary data folder, HTTP on one free port and the website (HTTPS on "localhost", with a
/// certificate made at run time) on another, mail to a <see cref="FakeSmtpServer"/> and Google to a
/// <see cref="FakeGoogle"/>. <c>PAIRNETS_E2E_SERVER</c> picks a published pairnets-server(.exe) or its .dll;
/// otherwise the build output that the server project copies next to the tests is used.
/// </summary>
public sealed class ServerProcess : IAsyncDisposable
{
    public const string ProgramVariable = "PAIRNETS_E2E_SERVER";
    public const string MailFrom = "nest@example.com";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly TempDir _dir;
    private readonly Dictionary<string, string> _environment;
    private readonly List<string> _output = [];
    private Process? _process;

    private ServerProcess(TempDir dir, int httpPort, int httpsPort, string token, FakeSmtpServer smtp, FakeGoogle google)
    {
        _dir = dir;
        Token = token;
        Smtp = smtp;
        Google = google;
        ApiUrl = new Uri($"http://127.0.0.1:{httpPort}/");
        WebsiteUrl = new Uri($"https://localhost:{httpsPort}/");
        DataDir = dir.Combine("data");
        TlsDir = dir.Combine("tls");
        UpdateDir = dir.Combine("update");
        var updater = dir.Combine("update.sh");
        File.WriteAllText(updater, "#!/bin/sh\n# Stand-in: its presence turns on \"Update server\". It is never run here.\nexit 0\n");
        TestCertificates.Create("localhost").WriteLeafOnlyTo(TlsDir);
        _environment = new Dictionary<string, string>
        {
            ["Sync__Token"] = token,
            ["Sync__DataDir"] = DataDir,
            ["Sync__TlsDir"] = TlsDir,
            ["Sync__UpdateDir"] = UpdateDir,
            ["Sync__UpdaterScript"] = updater,
            ["Sync__PurgeInitialDelay"] = "01:00:00",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort}",
            ["Sync__HttpsUrl"] = $"https://127.0.0.1:{httpsPort}",
            ["Sync__PublicUrl"] = $"https://localhost:{httpsPort}",
            ["Sync__SmtpHost"] = "127.0.0.1",
            ["Sync__SmtpPort"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sync__SmtpUseTls"] = "false",
            ["Sync__SmtpFrom"] = MailFrom,
            ["Sync__GoogleClientId"] = FakeGoogle.ClientId,
            ["Sync__GoogleClientSecret"] = FakeGoogle.ClientSecret,
            ["Sync__GoogleAuthUrl"] = google.AuthUrl,
            ["Sync__GoogleTokenUrl"] = google.TokenUrl,
            // Every request gets its log line (health and the device list are only logged at Debug otherwise).
            ["Logging__LogLevel__Pairnets.Server.Web.RequestLoggingMiddleware"] = "Debug",
        };
    }

    /// <summary>The plain-HTTP API, as the apps reach it behind the tunnel.</summary>
    public Uri ApiUrl { get; }

    /// <summary>The nest's website: https://localhost:&lt;port&gt;/ (also its Sync:PublicUrl).</summary>
    public Uri WebsiteUrl { get; }

    public string Token { get; }

    public string DataDir { get; }

    public string TlsDir { get; }

    /// <summary>Where "Update server" drops its request file.</summary>
    public string UpdateDir { get; }

    public FakeSmtpServer Smtp { get; }

    public FakeGoogle Google { get; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Everything the server and its command line printed so far, all runs together.</summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_output)
                return _output.ToList();
        }
    }

    public static async Task<ServerProcess> StartAsync()
    {
        var smtp = new FakeSmtpServer();
        var google = await FakeGoogle.StartAsync();
        for (var attempt = 1; ; attempt++)
        {
            var server = new ServerProcess(new TempDir("proc"), FreePort(), FreePort(),
                "e2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), smtp, google);
            try
            {
                await server.LaunchAsync();
                return server;
            }
            catch (Exception) when (attempt < 3 && server.Output.Any(l => l.Contains("address already in use", StringComparison.OrdinalIgnoreCase)))
            {
                // Another test took one of the free ports in the meantime: new ports, new try.
                await server.StopAsync();
                server._dir.Dispose();
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>Starts the server again on the same ports and data folder (after <see cref="StopAsync"/>).</summary>
    public async Task StartAgainAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await LaunchAsync();
                return;
            }
            catch (InvalidOperationException) when (attempt < 10 && !IsRunning)
            {
                await Task.Delay(500); // the old process may still hold the port for a moment
            }
        }
    }

    /// <summary>Stops the server: politely on Linux and macOS (SIGTERM, like systemctl stop), at once on Windows.</summary>
    public async Task StopAsync()
    {
        if (_process is not { } process)
            return;
        _process = null;
        if (!process.HasExited)
        {
            if (!OperatingSystem.IsWindows())
            {
                using var kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                    ?? throw new InvalidOperationException("kill did not start.");
                await kill.WaitForExitAsync();
            }
            else
            {
                Kill(process);
            }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await process.WaitForExitAsync();
            }
        }
        process.WaitForExit(); // lets the output readers finish
        process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it ended by itself in the meantime
        }
    }

    /// <summary>
    /// Runs the same program as a maintenance command: <c>pairnets-server &lt;args&gt; --data-dir &lt;data&gt;</c>.
    /// It gets none of the server's settings (no token): the commands need none.
    /// </summary>
    public async Task<CliResult> RunCliAsync(params string[] args)
    {
        var start = StartInfo([.. args, "--data-dir", DataDir], withServerSettings: false);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The server program did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(wait.Token);
        var result = new CliResult(process.ExitCode, await output, await error);
        lock (_output)
            _output.AddRange((result.Output + result.Error).Split('\n'));
        return result;
    }

    /// <summary>Fails when anything the server or its commands printed contains one of <paramref name="secrets"/>.</summary>
    public void AssertNeverPrinted(IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            var line = Output.FirstOrDefault(l => l.Contains(secret, StringComparison.Ordinal));
            Assert.True(line is null, $"The server printed a secret (token or key) in its output: {line?.Replace(secret, "<secret>", StringComparison.Ordinal)}");
        }
    }

    private async Task LaunchAsync()
    {
        var start = StartInfo([], withServerSettings: true);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        if (!process.Start())
            throw new InvalidOperationException("The server program did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = ApiUrl, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            if (process.HasExited)
            {
                process.WaitForExit();
                _process = null;
                throw new InvalidOperationException($"The server stopped while starting (exit {process.ExitCode}):\n{string.Join('\n', Output.TakeLast(40))}");
            }
            try
            {
                if (await http.GetStringAsync("api/health") == "ok")
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // not listening yet
            }
            if (DateTime.UtcNow > deadline)
            {
                await StopAsync();
                throw new TimeoutException($"The server did not answer /api/health within {StartTimeout.TotalSeconds:0} s:\n{string.Join('\n', Output.TakeLast(40))}");
            }
            await Task.Delay(100);
        }
    }

    private void Keep(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.Add(line);
    }

    private ProcessStartInfo StartInfo(IEnumerable<string> args, bool withServerSettings)
    {
        var (file, prefix) = Program();
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir.Path,
        };
        foreach (var arg in prefix.Concat(args))
            start.ArgumentList.Add(arg);
        // Nothing from the test run's own environment may change how the server behaves.
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("Sync__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Sync:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase)
                || key is "SYNC_TOKEN" or "PUBLIC_URL" or "PAIRNETS_PUBLIC_URL" or "DOTNET_ENVIRONMENT" or "urls" or "INVOCATION_ID" or "JOURNAL_STREAM")
            {
                start.Environment.Remove(key);
            }
        }
        if (withServerSettings)
        {
            foreach (var (key, value) in _environment)
                start.Environment[key] = value;
        }
        return start;
    }

    /// <summary>The program to run: a published server from PAIRNETS_E2E_SERVER, or the build output next to the tests.</summary>
    public static (string File, string[] Prefix) Program()
    {
        var custom = Environment.GetEnvironmentVariable(ProgramVariable);
        var path = string.IsNullOrWhiteSpace(custom) ? Path.Combine(AppContext.BaseDirectory, "pairnets-server.dll") : Path.GetFullPath(custom);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The server program is missing: {path}. Build the solution, or point {ProgramVariable} at a published pairnets-server.", path);
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? (DotnetHost(), [path]) : (path, []);
    }

    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, name)))
            return Path.Combine(root, name);
        return "dotnet";
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Google.DisposeAsync();
        await Smtp.DisposeAsync();
        _dir.Dispose();
    }
}
