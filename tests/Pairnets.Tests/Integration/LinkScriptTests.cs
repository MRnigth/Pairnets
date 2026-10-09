using System.Diagnostics;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Runs the real deploy/pairnets-link.sh (Linux only: it is the server's "link to your Pairnets account" step of
/// install.sh) against a fake of the service's add-a-server calls, with a folder instead of /etc/pairnets. Checks what
/// a person sees, how it waits for the approval, and that the device code, the tunnel token and the server's key
/// never reach the screen or curl's command line (which every local user can read), and land only in their two
/// private files.
/// </summary>
public class LinkScriptTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly TempDir _dir = new("link-sh");

    public void Dispose() => _dir.Dispose();

    private string Conf => _dir.Combine("conf");

    private static readonly string[] Secrets = [FakeRelayService.DeviceCode, FakeRelayService.TunnelToken, FakeRelayService.NestKey];

    [Fact]
    public async Task AnApprovedServerGetsItsTunnelAndKeyInPrivateFilesOnly()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.PollAnswers.Enqueue(FakeRelayService.Pending);
        service.PollAnswers.Enqueue((200, service.Approved()));

        var (exit, output, error) = await Run(service);

        Assert.True(exit == 0, error);
        // What a person sees: one link to open anywhere, and the code to compare.
        Assert.Contains("Open this link on any device and sign in to your Pairnets account:", output);
        Assert.Contains($"    {service.Url}/add?code={FakeRelayService.UserCode}\n", output);
        Assert.Contains($"check that the page shows the code {FakeRelayService.UserCode} and press \"Add this server\"", output);
        Assert.Contains("the link works for 15 minutes", output);
        Assert.Contains("Added to your Pairnets account as \"soro\".", output);

        Assert.Equal(["relay.env", "tunnel.env"], Directory.GetFiles(Conf).Select(f => Path.GetFileName(f)).Order());
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(Conf));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Conf, "tunnel.env")));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Conf, "relay.env")));
        Assert.Equal($"TUNNEL_TOKEN={FakeRelayService.TunnelToken}\n", File.ReadAllText(Path.Combine(Conf, "tunnel.env")));
        var relay = File.ReadAllText(Path.Combine(Conf, "relay.env"));
        Assert.Contains($"\nSync__RelayNestId={RelayFixtures.NestId}\n", relay);
        Assert.Contains($"\nSync__RelayKey={FakeRelayService.NestKey}\n", relay);
        Assert.Contains($"\nSync__RelayServiceUrl={service.Url}\n", relay);
        Assert.DoesNotContain(FakeRelayService.TunnelToken, relay);

        // Nothing secret on screen or on curl's command line, and each secret in its own file only.
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, output);
            Assert.DoesNotContain(secret, error);
            Assert.DoesNotContain(secret, CurlArguments());
        }
        foreach (var file in Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) != "tunnel.env")
                Assert.DoesNotContain(FakeRelayService.TunnelToken, text);
            if (Path.GetFileName(file) != "relay.env")
                Assert.DoesNotContain(FakeRelayService.NestKey, text);
            Assert.DoesNotContain(FakeRelayService.DeviceCode, text);
        }

        // The calls: start (no secret in it), then a poll per interval with the device code in the body.
        var requests = service.Requests;
        Assert.Equal(["/v1/servers/start", "/v1/servers/poll", "/v1/servers/poll"], requests.Select(r => r.Path));
        Assert.Matches("""^\{"hostname":"[A-Za-z0-9_-]+","serverVersion":"1\.0\.48"\}$""", requests[0].Body);
        Assert.All(requests, r => Assert.Equal(("POST", "application/json", false), (r.Method, r.ContentType, r.HasOrigin)));
        Assert.All(requests.Skip(1), r => Assert.Equal($"{{\"deviceCode\":\"{FakeRelayService.DeviceCode}\"}}", r.Body));
        var polls = service.Polls();
        Assert.True(polls[1].At - polls[0].At >= TimeSpan.FromSeconds(0.9), $"polled again after {polls[1].At - polls[0].At}");
    }

    [Fact]
    public async Task ItSlowsDownWhenTheServiceAsksAndKeepsTryingWhenItIsBusy()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.PollAnswers.Enqueue(FakeRelayService.SlowDown);
        service.PollAnswers.Enqueue((503, new { error = "busy" }));
        service.PollAnswers.Enqueue((200, service.Approved()));

        var (exit, output, error) = await Run(service);

        Assert.True(exit == 0, error);
        Assert.Contains("No answer from Pairnets just now; still trying.", output);
        var polls = service.Polls();
        Assert.Equal(3, polls.Count);
        // Interval 1 s, plus 5 s after "slow_down" (RFC 8628), for every later poll.
        Assert.True(polls[1].At - polls[0].At >= TimeSpan.FromSeconds(5.9), $"after slow_down it polled again after {polls[1].At - polls[0].At}");
        Assert.True(polls[2].At - polls[1].At >= TimeSpan.FromSeconds(5.9), $"then after {polls[2].At - polls[1].At}");
        Assert.True(File.Exists(Path.Combine(Conf, "relay.env")));
    }

    [Fact]
    public async Task NotMineChangesNothing()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.PollAnswers.Enqueue(FakeRelayService.Pending);
        service.PollAnswers.Enqueue(FakeRelayService.Denied);

        var (exit, _, error) = await Run(service);

        Assert.Equal(1, exit);
        Assert.Contains("this server was not added (\"Not mine\" was pressed on the page). Nothing was changed", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task AnExpiredCodeSaysToStartAgain()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.PollAnswers.Enqueue(FakeRelayService.Expired);

        var (exit, _, error) = await Run(service);

        Assert.Equal(1, exit);
        Assert.Contains("the link expired before this server was added. Nothing was changed; run the installer again for a new link.", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task ItGivesUpWhenTheCodeExpiresWithoutAnAnswer()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.ExpiresIn = 3;
        service.PollAnswers.Enqueue(FakeRelayService.Pending);

        var watch = Stopwatch.StartNew();
        var (exit, output, error) = await Run(service);

        Assert.Equal(1, exit);
        Assert.Contains("the link expired before this server was added", error);
        Assert.Contains("the link works for 1 minute;", output);
        Assert.InRange(service.Polls().Count, 1, 4);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task WhatTheServiceRefusesIsShownAndNothingIsWritten()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.StartAnswer = (429, new { error = "rate_limited", message = "Too many servers were added from here. Try again in an hour." });

        var (exit, _, error) = await Run(service);

        Assert.Equal(1, exit);
        Assert.Contains("error: Too many servers were added from here. Try again in an hour.", error);
        Assert.Single(service.Requests);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task AnAnswerThatIsNotATokenOrKeyIsNeverWritten()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.PollAnswers.Enqueue((200, new { status = "approved", nestId = RelayFixtures.NestId, tunnelToken = "x y", nestKey = FakeRelayService.NestKey, serviceUrl = service.Url }));

        var (exit, _, error) = await Run(service);
        Assert.Equal(1, exit);
        Assert.Contains("not a tunnel token", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());

        // A key with its unused bits set (not strict base64url of 32 bytes) is refused the same way.
        var key = FakeRelayService.NestKey;
        var bent = key[..42] + (char)(key[42] + 1);
        service.PollAnswers.Clear();
        service.PollAnswers.Enqueue((200, new { status = "approved", nestId = RelayFixtures.NestId, tunnelToken = FakeRelayService.TunnelToken, nestKey = bent, serviceUrl = service.Url }));
        (exit, _, error) = await Run(service);
        Assert.Equal(1, exit);
        Assert.Contains("not a server key", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task OnlyALinkOnTheServiceItselfIsShown()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();
        service.VerificationLink = "https://pairnets.example.net/add?code=" + FakeRelayService.UserCode;

        var (exit, output, error) = await Run(service);

        Assert.True(exit == 0, error);
        Assert.DoesNotContain("pairnets.example.net", output);
        Assert.Contains($"    {service.Url}/add?code={FakeRelayService.UserCode}\n", output);
    }

    [Fact]
    public async Task OnlyAnHttpsServiceUnlessItIsThisMachine()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeRelayService.StartAsync();

        var (exit, _, error) = await Run(service, serviceUrl: "http://sync.example.com");

        Assert.Equal(1, exit);
        Assert.Contains("the Pairnets service must be an https address", error);
        Assert.Empty(service.Requests);
    }

    /// <summary>
    /// Runs a copy of the script (with a VERSION file next to it, as in a release folder) and curl through a stand-in
    /// that writes down its command line before it runs the real curl. Standard input is closed: nothing is typed.
    /// </summary>
    private async Task<(int Exit, string Output, string Error)> Run(FakeRelayService service, string? serviceUrl = null)
    {
        var bin = _dir.Combine("bin");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "curl");
        File.WriteAllText(shim, $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{_dir.Combine("curl-args")}'\nexec '{RealCurl()}' \"$@\"\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(shim, Private | UnixFileMode.UserExecute);
        var release = _dir.Combine("release");
        Directory.CreateDirectory(release);
        var script = Path.Combine(release, "pairnets-link.sh");
        File.Copy(ScriptPath(), script, overwrite: true);
        File.WriteAllText(Path.Combine(release, "VERSION"), "1.0.48\n");

        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(script);
        start.Environment["PAIRNETS_CONF_DIR"] = Conf;
        start.Environment["PAIRNETS_SERVICE_URL"] = serviceUrl ?? service.Url;
        start.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private string CurlArguments()
    {
        var log = _dir.Combine("curl-args");
        Assert.True(File.Exists(log), "curl was never run");
        return File.ReadAllText(log);
    }

    private static string RealCurl() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, "curl")).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("curl is not installed");

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "deploy", "pairnets-link.sh");
            if (File.Exists(script))
                return script;
        }
        throw new FileNotFoundException("deploy/pairnets-link.sh not found above " + AppContext.BaseDirectory);
    }
}
