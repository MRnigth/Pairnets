using System.Diagnostics;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Runs the real deploy/linux-desktop/install-desktop.sh (Linux only) with a stand-in program and a home folder
/// of its own: where the program goes (--program-dir), that installing again keeps that place, and that a new
/// place moves it, with the command, the app menu entry and "start at login" following.
/// </summary>
public class InstallDesktopScriptTests : IDisposable
{
    private readonly TempDir _dir = new("install-desktop-sh");

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private string Link => Path.Combine(Home, ".local", "bin", "pairnets");

    private string MenuEntry => Path.Combine(Home, ".local", "share", "applications", "pairnets.desktop");

    private string Autostart => Path.Combine(Home, ".config", "autostart", "pairnets.desktop");

    [Fact]
    public async Task WithoutATerminalItGoesToTheHomeFolderWithoutAsking()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return;
        var (exit, output, error) = await RunInstall();
        Assert.True(exit == 0, error);
        Assert.DoesNotContain("Where should the Pairnets program go?", output + error);
        var expected = Path.Combine(Home, ".local", "opt", "pairnets", "Pairnets");
        Assert.Equal(expected, ResolveLink());
        Assert.Contains($"Exec=\"{expected}\" %u", File.ReadAllText(MenuEntry));
    }

    [Fact]
    public async Task TheChosenFolderIsKeptWhenInstallingAgainAndANewOneMovesIt()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return;
        var first = _dir.Combine("drive-a", "pairnets");
        var (exit, output, error) = await RunInstall("--program-dir", first);
        Assert.True(exit == 0, error);
        Assert.Contains($"Pairnets is installed in {first}.", output);
        Assert.Equal(Path.Combine(first, "Pairnets"), ResolveLink());
        Assert.Contains($"Exec=\"{first}/Pairnets\" %u", File.ReadAllText(MenuEntry));

        // "Start Pairnets when I log in" was on: it follows the program.
        Directory.CreateDirectory(Path.GetDirectoryName(Autostart)!);
        File.WriteAllText(Autostart, $"[Desktop Entry]\nType=Application\nName=Pairnets\nExec=\"{first}/Pairnets\"\n");

        // Installing again (an update by hand) keeps the place.
        (exit, _, error) = await RunInstall();
        Assert.True(exit == 0, error);
        Assert.Equal(Path.Combine(first, "Pairnets"), ResolveLink());

        // A new place moves it: the old program goes, the command, menu entry and autostart follow.
        var second = _dir.Combine("drive-b", "apps", "pairnets");
        (exit, output, error) = await RunInstall("--program-dir=" + second);
        Assert.True(exit == 0, error);
        Assert.Contains($"Moved Pairnets from {first} to {second}.", output);
        Assert.Equal(Path.Combine(second, "Pairnets"), ResolveLink());
        Assert.False(Directory.Exists(first));
        Assert.Contains($"Exec=\"{second}/Pairnets\" %u", File.ReadAllText(MenuEntry));
        Assert.Contains($"Exec=\"{second}/Pairnets\"", File.ReadAllText(Autostart));
    }

    [Fact]
    public async Task AFolderItCannotUseIsRefusedBeforeAnythingChanges()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return;
        var (exit, _, error) = await RunInstall("--program-dir", "pairnets");
        Assert.Equal(1, exit);
        Assert.Contains("must be a full path", error);

        var full = _dir.Combine("full");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "notes.txt"), "mine");
        (exit, _, error) = await RunInstall("--program-dir", full);
        Assert.Equal(1, exit);
        Assert.Contains("is not empty", error);

        (exit, _, error) = await RunInstall("--program-dir", "/usr/lib/pairnets");
        Assert.Equal(1, exit);
        Assert.Contains("is a system folder", error);
        Assert.False(File.Exists(Link) || Directory.Exists(Path.Combine(Home, ".local", "opt")));

        (exit, _, error) = await RunInstall("--program-dir");
        Assert.Equal(2, exit);
        Assert.Contains("--program-dir needs a folder", error);

        (exit, _, error) = await RunInstall("--frobnicate");
        Assert.Equal(2, exit);
        Assert.Contains("unknown option: --frobnicate", error);
    }

    private string ResolveLink() => new FileInfo(Link).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "(no link)";

    /// <summary>The script next to a stand-in program (a few bytes of shell) and icon, as the release package has them.</summary>
    private async Task<(int Exit, string Output, string Error)> RunInstall(params string[] args)
    {
        var package = _dir.Combine("pkg");
        Directory.CreateDirectory(package);
        var script = Path.Combine(package, "install-desktop.sh");
        File.Copy(ScriptPath(), script, overwrite: true);
        var program = Path.Combine(package, "Pairnets");
        if (!File.Exists(program))
        {
            File.WriteAllText(program, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(Path.Combine(package, "pairnets.png"), [0x89, 0x50, 0x4E, 0x47]);
        }
        Directory.CreateDirectory(Home);
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(script);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["HOME"] = Home;
        // No desktop session: the script must not start the stand-in, and the user's real one is not touched.
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment.Remove("XDG_DATA_HOME");
        start.Environment.Remove("XDG_CONFIG_HOME");
        start.Environment.Remove("XDG_CURRENT_DESKTOP");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
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
            var script = Path.Combine(dir.FullName, "deploy", "linux-desktop", "install-desktop.sh");
            if (File.Exists(script))
                return script;
        }
        throw new FileNotFoundException("deploy/linux-desktop/install-desktop.sh not found above " + AppContext.BaseDirectory);
    }
}
