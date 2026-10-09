using System.Diagnostics;
using System.Text.Json;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Runs the real deploy/update.sh (Linux only: it is the Ubuntu server's updater) against a local
/// release folder, and checks what it reports to the apps and writes to its log.
/// </summary>
public class UpdateScriptTests : IDisposable
{
    private readonly TempDir _dir = new("update-sh");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void NewerReleaseIsCheckedAndLoggedStepByStep()
    {
        if (!OperatingSystem.IsLinux())
            return;
        MakeRelease("1.0.99", corrupt: false);
        var (state, message) = Run();
        Assert.Equal("succeeded", state);
        Assert.Equal("Would install 1.0.99 (dry run).", message);
        var log = File.ReadAllText(_dir.Combine("update", "update.log"));
        Assert.Contains("=== update.sh started: installed version 1.0.29", log);
        Assert.Contains("checksum OK", log);
        Assert.Contains("installed version 1.0.29, newest release 1.0.99", log);
        Assert.Contains("=== update.sh finished", log);
        Assert.False(File.Exists(_dir.Combine("update", "request")));

        // A second try right away is refused by the 10-minute limit, and says so.
        (state, message) = Run();
        Assert.Equal("failed", state);
        Assert.Contains("less than 10 minutes", message);
    }

    [Fact]
    public void AnUnexpectedErrorIsReportedInsteadOfLeavingTheAppWaiting()
    {
        if (!OperatingSystem.IsLinux())
            return;
        MakeRelease("1.0.99", corrupt: true); // matching checksum, but not a real archive: tar fails
        var (state, message) = Run();
        Assert.Equal("failed", state);
        Assert.Contains("update.sh stopped unexpectedly at line", message);
        Assert.Contains("tar", message);
        Assert.Contains("status: failed", File.ReadAllText(_dir.Combine("update", "update.log")));
    }

    [Fact]
    public void ADownloadThatDoesNotMatchItsChecksumChangesNothing()
    {
        if (!OperatingSystem.IsLinux())
            return;
        MakeRelease("1.0.99", corrupt: false);
        File.AppendAllText(_dir.Combine("release", "pairnets-server-linux-x64.tar.gz"), "tampered");
        var (state, message) = Run();
        Assert.Equal("failed", state);
        Assert.Contains("did not match its checksum", message);
    }

    [Fact]
    public void SecretsInTheInstallOutputNeverReachTheLog()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // Fake values only. The tunnel token is built here so no token-shaped text sits in the repo.
        var tunnelToken = "eyJ" + "hIjoi" + new string('Q', 64) + "==";
        var deviceCode = "psd_" + new string('d', 43);
        string[] secrets =
        [
            "fake.shared.token", "fake.sync.value", "fake.tunnel.value", tunnelToken, "fake.setup.code",
            "fake.smtp.pass", "fake.google.secret", "first.fake.one", "second.fake.one", "fake.query.token",
            "fake.relay.key", deviceCode,
        ];
        string[] lines =
        [
            "  Token:       fake.shared.token",
            "SYNC_TOKEN=fake.sync.value",
            "TUNNEL_TOKEN=fake.tunnel.value",
            "cloudflared service install " + tunnelToken,
            "       https://sync.example.com/setup#code=fake.setup.code",
            "Sync__SmtpPassword=fake.smtp.pass",
            "Sync__GoogleClientSecret=fake.google.secret",
            "SYNC_TOKEN=first.fake.one TUNNEL_TOKEN=second.fake.one",
            "https://sync.example.com/link?token=fake.query.token&code=fake.setup.code",
            "Sync__RelayKey=fake.relay.key",
            "polling with " + deviceCode,
            "Keeping the configured address 127.0.0.1:5075",
        ];
        var script = "#!/bin/sh\n" + string.Concat(lines.Select(l => $"echo '{l}'\n")) + "echo 'SYNC_TOKEN=fake.sync.value' >&2\n";
        MakeRelease("1.0.99", corrupt: false, installScript: script);

        var (state, message) = Run(dryRun: false);

        Assert.Equal("succeeded", state);
        Assert.Equal("Updated from 1.0.29 to 1.0.99.", message);
        var log = File.ReadAllText(_dir.Combine("update", "update.log"));
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, log);
        Assert.Contains("Token:       (hidden)", log);
        Assert.Contains("SYNC_TOKEN=(hidden) TUNNEL_TOKEN=(hidden)", log);
        Assert.Contains("cloudflared service install (hidden)", log);
        Assert.Contains("/setup#code=(hidden)", log);
        Assert.Contains("Sync__SmtpPassword=(hidden)", log);
        Assert.Contains("Sync__GoogleClientSecret=(hidden)", log);
        Assert.Contains("Sync__RelayKey=(hidden)", log);
        Assert.Contains("polling with (hidden)", log);
        Assert.Contains("Keeping the configured address 127.0.0.1:5075", log); // everything else is kept as it was
    }

    [Fact]
    public void AnInstallThatFailsIsReportedAsAFailedUpdate()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // install.sh exits non-zero when the server does not answer after the install.
        MakeRelease("1.0.99", corrupt: false, installScript: "#!/bin/sh\necho 'WARNING: the service did not answer'\nexit 1\n");

        var (state, message) = Run(dryRun: false);

        Assert.Equal("failed", state);
        Assert.Contains("Installing 1.0.99 did not finish", message);
        Assert.Contains("WARNING: the service did not answer", File.ReadAllText(_dir.Combine("update", "update.log")));
    }

    private void MakeRelease(string version, bool corrupt, string installScript = "#!/bin/sh\necho installed\n")
    {
        var release = _dir.Combine("release");
        var payload = Path.Combine(release, "pairnets-server-linux-x64");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "VERSION"), version + "\n");
        File.WriteAllText(Path.Combine(payload, "install.sh"), installScript);
        Bash($"chmod +x '{Path.Combine(payload, "install.sh")}'");
        Directory.CreateDirectory(_dir.Combine("install"));
        File.WriteAllText(_dir.Combine("install", "VERSION"), "1.0.29\n");
        var archive = Path.Combine(release, "pairnets-server-linux-x64.tar.gz");
        if (corrupt)
            File.WriteAllText(archive, "not an archive");
        else
            Bash($"tar -czf '{archive}' -C '{release}' pairnets-server-linux-x64");
        Bash($"cd '{release}' && sha256sum pairnets-server-linux-x64.tar.gz > SHA256SUMS.txt");
    }

    private (string State, string Message) Run(bool dryRun = true)
    {
        var updateDir = _dir.Combine("update");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "request"), string.Empty);
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(ScriptPath());
        start.Environment["PAIRNETS_BASE_URL"] = _dir.Combine("release");
        start.Environment["PAIRNETS_UPDATE_DIR"] = updateDir;
        start.Environment["PAIRNETS_INSTALL_DIR"] = _dir.Combine("install");
        if (dryRun)
            start.Environment["PAIRNETS_UPDATE_DRY_RUN"] = "1";
        else
            start.Environment.Remove("PAIRNETS_UPDATE_DRY_RUN");
        using var process = Process.Start(start)!;
        process.WaitForExit(60_000);
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(updateDir, "status.json")));
        return (status.RootElement.GetProperty("state").GetString()!, status.RootElement.GetProperty("message").GetString()!);
    }

    private static void Bash(string command)
    {
        var start = new ProcessStartInfo("bash") { RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        process.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0, $"{command}: {process.StandardError.ReadToEnd()}");
    }

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "deploy", "update.sh");
            if (File.Exists(script))
                return script;
        }
        throw new FileNotFoundException("deploy/update.sh not found above " + AppContext.BaseDirectory);
    }
}
