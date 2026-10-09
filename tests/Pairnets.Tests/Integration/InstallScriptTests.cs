using System.Diagnostics;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Runs the real deploy/install.sh (Linux only: it installs the Ubuntu server) for what it checks before
/// it touches anything: its options. The script runs from a copy in an empty folder, so even as root it
/// stops before changing the machine (there is no pairnets-server next to it).
/// </summary>
public class InstallScriptTests : IDisposable
{
    private readonly TempDir _dir = new("install-sh");

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("--public-url")]
    [InlineData("--bind")]
    [InlineData("--port")]
    public async Task AnOptionWithoutItsValueSaysSo(string option)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (exit, _, error) = await RunInstall(option);
        Assert.Equal(2, exit);
        Assert.Contains($"error: {option} needs a value, for example: {option} ", error);
    }

    [Fact]
    public async Task AValueThatIsAnotherOptionOrEmptyIsMissingToo()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (exit, _, error) = await RunInstall("--public-url", "--cloudflare-tunnel");
        Assert.Equal(2, exit);
        Assert.Contains("--public-url needs a value", error);

        (exit, _, error) = await RunInstall("--public-url=");
        Assert.Equal(2, exit);
        Assert.Contains("--public-url needs a value", error);
    }

    [Fact]
    public async Task AnUnknownOptionShowsTheUsage()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (exit, _, error) = await RunInstall("--frobnicate");
        Assert.Equal(2, exit);
        Assert.Contains("error: unknown option: --frobnicate", error);
        Assert.Contains("Usage: sudo ./install.sh", error);

        // A bare address gets a hint at the option it was meant for.
        (exit, _, error) = await RunInstall("https://sync.example.com");
        Assert.Equal(2, exit);
        Assert.Contains("--public-url https://sync.example.com", error);
    }

    [Fact]
    public async Task HelpShowsTheUsage()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (exit, output, _) = await RunInstall("--help");
        Assert.Equal(0, exit);
        Assert.Contains("Usage: sudo ./install.sh", output);
        Assert.Contains("--public-url https://<name>", output);
        Assert.Contains("--link ", output);
        Assert.Contains("First install: links this server to your Pairnets account", output);
    }

    [Theory]
    [InlineData("--port 5075 --public-url=https://sync.example.com --cloudflare-tunnel")]
    [InlineData("")] // a first install links the server to a Pairnets account
    [InlineData("--link")]
    [InlineData("--link --port 5075")]
    public async Task ValidOptionsGetPastTheChecks(string line)
    {
        if (!OperatingSystem.IsLinux())
            return;
        // Not root (or, as root, no server binary next to the script): it stops at the next check.
        var (exit, _, error) = await RunInstall(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(1, exit);
        Assert.DoesNotContain("needs a value", error);
        Assert.DoesNotContain("unknown option", error);
        Assert.True(error.Contains("run as root") || error.Contains("pairnets-server binary not found"), error);
    }

    [Theory]
    [InlineData("--public-url", "https://sync.example.com", "use --link (your Pairnets account, sync.pairnets.app) or --public-url (your own domain), not both")]
    [InlineData("--bind", "192.0.2.10", "--link sets up the tunnel itself, which listens on 127.0.0.1; leave out --bind and --cloudflare-tunnel")]
    [InlineData("--cloudflare-tunnel", null, "--link sets up the tunnel itself")]
    [InlineData("--port", "6000", "--link uses port 5075 (where Pairnets reaches the server); leave out --port")]
    public async Task LinkingGoesWithoutTheOwnDomainOptions(string option, string? value, string message)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (exit, _, error) = await RunInstall(value is null ? ["--link", option] : ["--link", option, value]);
        Assert.Equal(2, exit);
        Assert.Contains("error: " + message, error);
        Assert.Contains("Usage: sudo ./install.sh", error);

        // --link takes no value.
        (exit, _, error) = await RunInstall("--link=yes");
        Assert.Equal(2, exit);
        Assert.Contains("unknown option: --link=yes", error);
    }

    private async Task<(int Exit, string Output, string Error)> RunInstall(params string[] args)
    {
        var script = _dir.Combine("install.sh");
        File.Copy(ScriptPath(), script, overwrite: true);
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment.Remove("TUNNEL_TOKEN");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "deploy", "install.sh");
            if (File.Exists(script))
                return script;
        }
        throw new FileNotFoundException("deploy/install.sh not found above " + AppContext.BaseDirectory);
    }
}
