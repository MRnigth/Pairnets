using System.Diagnostics;
using System.Text;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// The server that install.sh put on the Linux test box, running under systemd. Everything about it comes from the
/// PAIRNETS_E2E_* variables (docs/PREDEPLOY.md). Commands run on the box through PAIRNETS_E2E_SHELL ("wsl.exe -d
/// pairnets-test -u root --" from Windows, "sudo" in CI) followed by <c>bash -c "&lt;script&gt;"</c>; the script itself
/// travels base64-encoded, so no quoting layer in between can change it.
/// </summary>
public sealed class InstalledLinuxTarget : IServerTarget
{
    private const string Program = "/opt/pairnets/pairnets-server";
    private const string ServiceUser = "pairnets";

    private readonly string[] _shell;
    private readonly string _dataDir;
    private readonly string _service;
    private readonly string _mailDir;

    private InstalledLinuxTarget(Uri api, Uri website, string token, string[] shell, string dataDir, string service, string mailDir, string? report)
    {
        ApiUrl = api;
        WebsiteUrl = website;
        Token = token;
        _shell = shell;
        _dataDir = dataDir.TrimEnd('/');
        _service = service;
        _mailDir = mailDir.TrimEnd('/');
        ReportPath = report;
    }

    public string Name => E2EEnvironment.InstalledLinux;

    public Uri ApiUrl { get; }

    public Uri WebsiteUrl { get; }

    public string Token { get; }

    public bool RealUpdater => true;

    /// <summary>Where the tour writes what it covered (PAIRNETS_E2E_REPORT).</summary>
    public string? ReportPath { get; }

    public static InstalledLinuxTarget FromEnvironment()
    {
        static Uri Address(string name)
        {
            var text = E2EEnvironment.Required(name);
            return new Uri(text.EndsWith('/') ? text : text + "/");
        }

        return new InstalledLinuxTarget(
            Address("PAIRNETS_E2E_URL"),
            Address("PAIRNETS_E2E_WEBSITE_URL"),
            E2EEnvironment.Required("PAIRNETS_E2E_TOKEN"),
            E2EEnvironment.Required("PAIRNETS_E2E_SHELL").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            E2EEnvironment.Required("PAIRNETS_E2E_DATA_DIR"),
            E2EEnvironment.Required("PAIRNETS_E2E_SERVICE"),
            E2EEnvironment.Required("PAIRNETS_E2E_MAIL_DIR"),
            Environment.GetEnvironmentVariable("PAIRNETS_E2E_REPORT") is { Length: > 0 } report ? report : null);
    }

    /// <summary>Runs a bash script as root on the box.</summary>
    public async Task<CliResult> RunOnBoxAsync(string script, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(_shell[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in _shell.Skip(1))
            start.ArgumentList.Add(arg);
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script))} | base64 -d | bash");
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{_shell[0]} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var wait = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(wait.Token);
        return new CliResult(process.ExitCode, (await output).ReplaceLineEndings("\n"), (await error).ReplaceLineEndings("\n"));
    }

    private async Task<string> MustRunAsync(string script)
    {
        var result = await RunOnBoxAsync(script);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"On the test box, this failed ({result.ExitCode}): {script}\n{result.Output}{result.Error}");
        return result.Output;
    }

    public async Task<IReadOnlyList<RawMail>> MailsAsync()
    {
        // The fake mail server writes each message as <unix-ms>-<n>.eml; oldest first, base64 so nothing gets mangled.
        var listing = await MustRunAsync($"""
            cd {Quote(_mailDir)} 2>/dev/null || exit 0
            for f in $(ls -1 | grep '\.eml$' | sort -t- -k1,1n -k2,2n); do printf '%s\n' "$f"; base64 -w0 "$f"; printf '\n'; done
            """);
        var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var mails = new List<RawMail>();
        for (var i = 0; i + 1 < lines.Length; i += 2)
            mails.Add(RawMail.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(lines[i + 1].Trim()))));
        return mails;
    }

    public Task<CliResult> RunCliAsync(params string[] args) =>
        RunOnBoxAsync($"runuser -u {ServiceUser} -- {Program} {string.Join(' ', args.Select(Quote))} --data-dir {Quote(_dataDir)}");

    public Task StopServerAsync() => MustRunAsync($"systemctl stop {Quote(_service)}");

    public async Task StartServerAsync()
    {
        await MustRunAsync($"systemctl start {Quote(_service)}");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = ApiUrl, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                if (await http.GetStringAsync("api/health") == "ok")
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"{_service} did not answer /api/health within 90 s after systemctl start.", ex);
            }
            await Task.Delay(200);
        }
    }

    public Task WriteDataFileAsync(string relative, byte[] content)
    {
        var path = _dataDir + "/" + relative;
        var dir = path[..path.LastIndexOf('/')];
        return MustRunAsync($"runuser -u {ServiceUser} -- mkdir -p {Quote(dir)} && echo {Convert.ToBase64String(content)} | base64 -d | runuser -u {ServiceUser} -- tee {Quote(path)} > /dev/null");
    }

    public Task MoveDataFileAsync(string from, string to) =>
        MustRunAsync($"runuser -u {ServiceUser} -- mv {Quote(_dataDir + "/" + from)} {Quote(_dataDir + "/" + to)}");

    /// <summary>The real updater takes the request at once and reports in status.json; either one shows it was asked.</summary>
    public async Task<bool> UpdateRequestedAsync() =>
        (await RunOnBoxAsync($"test -e {Quote(_dataDir + "/update/request")} || test -s {Quote(_dataDir + "/update/status.json")}")).ExitCode == 0;

    public async Task<IReadOnlyList<string>> ServerLogAsync() =>
        (await MustRunAsync($"journalctl -u {Quote(_service)} --no-pager -o cat")).Split('\n');

    /// <summary>The box logs at Information, so the polled requests are missing there: only the tour's side counts.</summary>
    public Task<bool> CollectServerSideAsync(TourRecord record) => Task.FromResult(false);

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
