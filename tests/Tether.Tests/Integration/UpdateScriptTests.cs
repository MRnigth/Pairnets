using System.Diagnostics;
using System.Text.Json;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Integration;

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
        File.AppendAllText(_dir.Combine("release", "tether-server-linux-x64.tar.gz"), "tampered");
        var (state, message) = Run();
        Assert.Equal("failed", state);
        Assert.Contains("did not match its checksum", message);
    }

    private void MakeRelease(string version, bool corrupt)
    {
        var release = _dir.Combine("release");
        var payload = Path.Combine(release, "tether-server-linux-x64");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "VERSION"), version + "\n");
        File.WriteAllText(Path.Combine(payload, "install.sh"), "#!/bin/sh\necho installed\n");
        Directory.CreateDirectory(_dir.Combine("install"));
        File.WriteAllText(_dir.Combine("install", "VERSION"), "1.0.29\n");
        var archive = Path.Combine(release, "tether-server-linux-x64.tar.gz");
        if (corrupt)
            File.WriteAllText(archive, "not an archive");
        else
            Bash($"tar -czf '{archive}' -C '{release}' tether-server-linux-x64");
        Bash($"cd '{release}' && sha256sum tether-server-linux-x64.tar.gz > SHA256SUMS.txt");
    }

    private (string State, string Message) Run()
    {
        var updateDir = _dir.Combine("update");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "request"), string.Empty);
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(ScriptPath());
        start.Environment["TETHER_BASE_URL"] = _dir.Combine("release");
        start.Environment["TETHER_UPDATE_DIR"] = updateDir;
        start.Environment["TETHER_INSTALL_DIR"] = _dir.Combine("install");
        start.Environment["TETHER_UPDATE_DRY_RUN"] = "1";
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
