using System.Diagnostics;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Runs the real deploy/pairnets-name.sh (Linux only: it is the server's free-name tool, used by install.sh --name)
/// against a fake name service, with a folder instead of /etc/pairnets and the typed answers read from a file. Checks
/// that the tunnel token and the name's key end up only in their two private files, and never on screen or on curl's
/// command line (which every local user can read).
/// </summary>
public class NameScriptTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly TempDir _dir = new("name-sh");

    public void Dispose() => _dir.Dispose();

    private string Conf => _dir.Combine("conf");

    [Fact]
    public async Task AClaimKeepsTheTokenAndTheKeyOnlyInPrivateFiles()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();

        var (exit, output, error) = await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com");

        Assert.True(exit == 0, error);
        Assert.Contains("This nest's name is https://alice.pairnets.app", output);
        Assert.Equal(["name.env", "tunnel.env"], Directory.GetFiles(Conf).Select(f => Path.GetFileName(f)).Order());
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(Conf));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Conf, "tunnel.env")));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Conf, "name.env")));
        Assert.Equal($"TUNNEL_TOKEN={FakeNameService.TunnelToken}\n", File.ReadAllText(Path.Combine(Conf, "tunnel.env")));
        var nameEnv = File.ReadAllText(Path.Combine(Conf, "name.env"));
        Assert.Contains("PAIRNETS_NAME=alice\n", nameEnv);
        Assert.Contains("PAIRNETS_NAME_URL=https://alice.pairnets.app\n", nameEnv);
        Assert.Contains($"PAIRNETS_NAMES_URL={service.Url}\n", nameEnv);
        Assert.Contains($"PAIRNETS_NAME_KEY={FakeNameService.ManageKey}\n", nameEnv);
        Assert.DoesNotContain(FakeNameService.TunnelToken, nameEnv);

        // Nothing secret on screen, and the token in no other file at all.
        foreach (var secret in new[] { FakeNameService.TunnelToken, FakeNameService.ManageKey })
        {
            Assert.DoesNotContain(secret, output);
            Assert.DoesNotContain(secret, error);
        }
        foreach (var file in Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "tunnel.env"))
            Assert.DoesNotContain(FakeNameService.TunnelToken, File.ReadAllText(file));
        // The code went to the service in the body, never on curl's command line.
        Assert.DoesNotContain(FakeNameService.Code, CurlArguments());

        Assert.Equal(["/v1/claim/start", "/v1/claim"], service.Requests.Select(r => r.Path));
        Assert.Equal("""{"name":"alice","email":"you@example.com"}""", service.Requests[0].Body);
        Assert.Contains($"\"code\":\"{FakeNameService.Code}\"", service.Requests[1].Body);
        Assert.All(service.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task ItAsksForTheAddressAndForTheCodeAgainWhenItIsWrong()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();

        var (exit, output, error) = await Run(service, "you@example.com\n12345\n000000\n123 456\n", "claim", "Alice");

        Assert.True(exit == 0, error);
        Assert.Contains("Your email address:", output);
        Assert.Contains("The code is 6 digits; type it again.", output);
        Assert.Contains("That code is not right (4 tries left). Type it again.", output);
        Assert.Equal(["/v1/claim/start", "/v1/claim", "/v1/claim"], service.Requests.Select(r => r.Path));
        Assert.Contains("\"name\":\"alice\"", service.Requests[0].Body); // lowercased
        Assert.True(File.Exists(Path.Combine(Conf, "tunnel.env")));
    }

    [Fact]
    public async Task WhatTheServiceRefusesIsShownAndNothingIsWritten()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();
        service.StartAnswer = (409, new { error = "name_taken", message = "That name is taken. Pick another one." });

        var (exit, _, error) = await Run(service, "", "claim", "alice", "--email", "you@example.com");

        Assert.Equal(1, exit);
        Assert.Contains("error: That name is taken. Pick another one.", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task AnAnswerThatIsNotATokenIsNeverWritten()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();
        service.ClaimAnswer = new { name = "alice", public_url = "https://alice.pairnets.app", tunnel_token = "x y", manage_key = FakeNameService.ManageKey };

        var (exit, _, error) = await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com");

        Assert.Equal(1, exit);
        Assert.Contains("not a tunnel token", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());

        // An address for another name is refused the same way.
        service.ClaimAnswer = new { name = "alice", public_url = "https://mallory.pairnets.app", tunnel_token = FakeNameService.TunnelToken, manage_key = FakeNameService.ManageKey };
        (exit, _, error) = await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com");
        Assert.Equal(1, exit);
        Assert.Contains("an address this script does not expect", error);
        Assert.False(Directory.Exists(Conf) && Directory.EnumerateFiles(Conf).Any());
    }

    [Fact]
    public async Task ANestHasOneNameAtATime()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();
        Assert.Equal(0, (await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com")).Exit);

        var (exit, _, error) = await Run(service, "", "claim", "bob", "--email", "you@example.com");

        Assert.Equal(1, exit);
        Assert.Contains("this nest already has the name https://alice.pairnets.app", error);
        Assert.Contains("pairnets-name.sh release", error);
        Assert.Equal(2, service.Requests.Count);
    }

    [Fact]
    public async Task RotateSendsTheKeyOnlyInAHeaderAndReplacesTheToken()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();
        Assert.Equal(0, (await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com")).Exit);

        var (exit, output, error) = await Run(service, "", "rotate");

        Assert.True(exit == 0, error);
        Assert.Contains("has a new token", output);
        Assert.Equal($"TUNNEL_TOKEN={FakeNameService.NewTunnelToken}\n", File.ReadAllText(Path.Combine(Conf, "tunnel.env")));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Conf, "tunnel.env")));
        var rotate = service.Requests.Last();
        Assert.Equal("/v1/rotate", rotate.Path);
        Assert.Equal("Bearer " + FakeNameService.ManageKey, rotate.Authorization);
        Assert.Equal("""{"name":"alice"}""", rotate.Body);
        Assert.DoesNotContain(FakeNameService.ManageKey, CurlArguments());
        Assert.DoesNotContain(FakeNameService.NewTunnelToken, output + error);
        // The key's config file for curl is gone again.
        Assert.Empty(Directory.GetFiles(_dir.Path, "auth", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReleaseAsksForTheNameThenForgetsIt()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();
        Assert.Equal(0, (await Run(service, FakeNameService.Code + "\n", "claim", "alice", "--email", "you@example.com")).Exit);

        var (exit, _, error) = await Run(service, "bob\n", "release");
        Assert.Equal(1, exit);
        Assert.Contains("nothing was changed", error);
        Assert.DoesNotContain(service.Requests, r => r.Method == "DELETE");
        Assert.True(File.Exists(Path.Combine(Conf, "name.env")));

        (exit, var output, error) = await Run(service, "alice\n", "release");
        Assert.True(exit == 0, error);
        Assert.Contains("The name https://alice.pairnets.app is given back.", output);
        var release = service.Requests.Last();
        Assert.Equal(("DELETE", "/v1/name", "Bearer " + FakeNameService.ManageKey), (release.Method, release.Path, release.Authorization));
        Assert.Empty(Directory.GetFiles(Conf));
        Assert.DoesNotContain(FakeNameService.ManageKey, CurlArguments());
    }

    [Fact]
    public async Task OnlyAnHttpsServiceUnlessItIsThisMachine()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();

        var (exit, _, error) = await Run(service, "", ["claim", "alice", "--email", "you@example.com"], serviceUrl: "http://names.example.com");

        Assert.Equal(1, exit);
        Assert.Contains("the name service must be an https address", error);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task WithoutATerminalItSaysWhatToDo()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();

        var (exit, _, error) = await Run(service, null, "claim", "alice", "--email", "you@example.com");

        Assert.Equal(1, exit);
        Assert.Contains("no terminal to type the emailed code into", error);
        Assert.Empty(service.Requests);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("alice-")]
    [InlineData("a--b")]
    [InlineData("al_ice")]
    public async Task ANameTheServiceWouldRefuseIsRefusedFirst(string name)
    {
        if (!OperatingSystem.IsLinux())
            return;
        await using var service = await FakeNameService.StartAsync();

        var (exit, _, error) = await Run(service, "", "claim", name, "--email", "you@example.com");

        Assert.Equal(1, exit);
        Assert.Contains("a name is 3 to 32 lowercase letters", error);
        Assert.Empty(service.Requests);
    }

    private Task<(int Exit, string Output, string Error)> Run(FakeNameService service, string? answers, params string[] args) =>
        Run(service, answers, args, serviceUrl: null);

    /// <summary>
    /// Runs the script with <paramref name="answers"/> as what is typed (null: there is no terminal), and curl
    /// through a stand-in that writes down its command line before it runs the real curl.
    /// </summary>
    private async Task<(int Exit, string Output, string Error)> Run(FakeNameService service, string? answers, string[] args, string? serviceUrl)
    {
        var bin = _dir.Combine("bin");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "curl");
        File.WriteAllText(shim, $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{_dir.Combine("curl-args")}'\nexec '{RealCurl()}' \"$@\"\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(shim, Private | UnixFileMode.UserExecute);
        var answersFile = _dir.Combine("answers");
        if (answers is null)
            File.Delete(answersFile);
        else
            File.WriteAllText(answersFile, answers);

        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(ScriptPath());
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["PAIRNETS_CONF_DIR"] = Conf;
        start.Environment["PAIRNETS_NAMES_URL"] = serviceUrl ?? service.Url;
        start.Environment["PAIRNETS_TTY"] = answersFile;
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
            var script = Path.Combine(dir.FullName, "deploy", "pairnets-name.sh");
            if (File.Exists(script))
                return script;
        }
        throw new FileNotFoundException("deploy/pairnets-name.sh not found above " + AppContext.BaseDirectory);
    }
}
