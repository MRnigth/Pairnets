using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>"Program folder: Move…" in the Linux app: copy, check, repoint, and the old folder removed by the copy.</summary>
public sealed class LinuxAppPlaceTests : IDisposable
{
    private readonly TempDir _dir = new("app-place");

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private string AppDir => _dir.Combine("home", ".local", "opt", "pairnets");

    private string AppFile => Path.Combine(AppDir, "Pairnets");

    private string LocalDir => _dir.Combine("home", ".local", "share", "Pairnets");

    /// <summary>The app as install-desktop.sh leaves it: the program, its command, its menu entry, and start at login.</summary>
    private LinuxAppPlace Installed(string? dataHome = null)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllBytes(AppFile, [.. Enumerable.Range(0, 5000).Select(i => (byte)i)]);
        File.WriteAllText(AppFile + ".old", "left by an update");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(AppFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var menu = Path.Combine(dataHome ?? Path.Combine(Home, ".local", "share"), "applications", "pairnets.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(menu)!);
        File.WriteAllText(menu, $"[Desktop Entry]\nType=Application\nName=Pairnets\nExec=\"{AppFile}\" %u\nIcon=pairnets\n");
        var autostart = Path.Combine(Home, ".config", "autostart", "pairnets.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(autostart)!);
        File.WriteAllText(autostart, $"[Desktop Entry]\nType=Application\nExec=\"{AppFile}\" --autostart\nX-GNOME-Autostart-enabled=true\n");
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.Combine(Home, ".local", "bin"));
            File.CreateSymbolicLink(Path.Combine(Home, ".local", "bin", "pairnets"), AppFile);
        }
        return new LinuxAppPlace(AppFile, Home, LocalDir, dataHome);
    }

    [Fact]
    public void TheRowShowsTheFolderUnderHome() =>
        Assert.Equal("~" + Path.DirectorySeparatorChar + Path.Combine(".local", "opt", "pairnets"), Installed().DisplayDir.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void AFolderThatCantHoldTheProgramIsTurnedDownWithTheReason()
    {
        var place = Installed();
        Assert.Equal("Choose a full folder path.", place.Check("relative/folder"));
        Assert.Equal("Pairnets is already in that folder.", place.Check(Path.GetDirectoryName(AppDir)!));
        Assert.Equal("Choose a folder outside Pairnets' own folder.", place.Check(AppDir));
        Assert.Contains("does not exist", place.Check(_dir.Combine("nowhere")), StringComparison.Ordinal);

        var full = _dir.Combine("full");
        Directory.CreateDirectory(Path.Combine(full, "pairnets"));
        File.WriteAllText(Path.Combine(full, "pairnets", "something"), "x");
        Assert.Contains("is not empty", place.Check(full), StringComparison.Ordinal);

        var good = _dir.Combine("drive");
        Directory.CreateDirectory(good);
        Assert.Null(place.Check(good, mountInfo: string.Empty));
    }

    [Fact]
    public void ADriveMountedNoexecIsFoundInMountinfo()
    {
        const string mountInfo = """
            22 1 8:2 / / rw,relatime shared:1 - ext4 /dev/sda2 rw
            40 22 8:17 / /mnt/data rw,nosuid,nodev,noexec,relatime shared:20 - ext4 /dev/sdb1 rw
            41 40 8:33 / /mnt/data/fast rw,relatime shared:21 - ext4 /dev/sdc1 rw
            42 22 8:49 / /media/my\040stick rw,noexec shared:22 - vfat /dev/sdd1 rw
            """;
        Assert.Equal("/mnt/data", LinuxAppPlace.NoExecMount(mountInfo, "/mnt/data/apps"));
        Assert.Equal("/mnt/data", LinuxAppPlace.NoExecMount(mountInfo, "/mnt/data"));
        Assert.Null(LinuxAppPlace.NoExecMount(mountInfo, "/mnt/data/fast/apps")); // the deeper mount allows programs
        Assert.Null(LinuxAppPlace.NoExecMount(mountInfo, "/home/me/apps"));
        Assert.Null(LinuxAppPlace.NoExecMount(mountInfo, "/mnt/database"));
        Assert.Equal("/media/my stick", LinuxAppPlace.NoExecMount(mountInfo, "/media/my stick/apps"));

        if (OperatingSystem.IsWindows())
            return; // the check below compares Linux paths
        var place = Installed();
        var drive = _dir.Combine("drive");
        Directory.CreateDirectory(drive);
        var info = $"22 1 8:2 / / rw shared:1 - ext4 /dev/sda2 rw\n40 22 8:17 / {drive} rw,noexec shared:20 - ext4 /dev/sdb1 rw\n";
        Assert.Contains("noexec", place.Check(drive, info), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveCopiesChecksAndPointsEverythingAtTheCopy()
    {
        var dataHome = _dir.Combine("xdg-data");
        var place = Installed(dataHome);
        var drive = _dir.Combine("drive");
        Directory.CreateDirectory(drive);

        var newApp = place.Move(drive, mountInfo: string.Empty);

        var newDir = Path.Combine(drive, "pairnets");
        Assert.Equal(Path.Combine(newDir, "Pairnets"), newApp);
        Assert.Equal(File.ReadAllBytes(AppFile), File.ReadAllBytes(newApp));
        Assert.False(File.Exists(newApp + ".old")); // what an update left behind is not carried along
        Assert.True(File.Exists(AppFile)); // the old one stays until the copy runs
        Assert.Contains($"Exec=\"{newApp}\" %u", File.ReadAllText(Path.Combine(dataHome, "applications", "pairnets.desktop")), StringComparison.Ordinal);
        Assert.Contains($"Exec=\"{newApp}\" --autostart", File.ReadAllText(Path.Combine(Home, ".config", "autostart", "pairnets.desktop")), StringComparison.Ordinal);
        Assert.True(File.Exists(LinuxAppPlace.MarkerPath(LocalDir)));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(newApp, new FileInfo(Path.Combine(Home, ".local", "bin", "pairnets")).LinkTarget);
            Assert.True(File.GetUnixFileMode(newApp).HasFlag(UnixFileMode.UserExecute));
        }

        // A second move into the same (now full) folder is turned down and changes nothing.
        var error = Assert.Throws<PlainMessageException>(() => new LinuxAppPlace(AppFile, Home, LocalDir, dataHome).Move(drive, string.Empty));
        Assert.Contains("is not empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCopyRemovesTheOldFolderOnlyWhenItRunsFromTheNewOne()
    {
        var place = Installed();
        var drive = _dir.Combine("drive");
        Directory.CreateDirectory(drive);
        var newApp = place.Move(drive, mountInfo: string.Empty);

        // Still the old app (the copy did not start): both stay, and so does the note.
        Assert.False(LinuxAppPlace.CleanUpOldCopy(LocalDir, AppFile));
        Assert.True(Directory.Exists(AppDir));
        Assert.True(File.Exists(LinuxAppPlace.MarkerPath(LocalDir)));

        Assert.True(LinuxAppPlace.CleanUpOldCopy(LocalDir, newApp));
        Assert.False(Directory.Exists(AppDir));
        Assert.True(File.Exists(newApp));
        Assert.False(File.Exists(LinuxAppPlace.MarkerPath(LocalDir)));
        Assert.False(LinuxAppPlace.CleanUpOldCopy(LocalDir, newApp)); // nothing left to do
    }

    [Fact]
    public void AnOldFolderWithoutThePairnetsProgramIsNeverRemoved()
    {
        var place = Installed();
        var drive = _dir.Combine("drive");
        Directory.CreateDirectory(drive);
        var newApp = place.Move(drive, mountInfo: string.Empty);
        File.Delete(AppFile); // something else is in that folder now
        File.WriteAllText(Path.Combine(AppDir, "keep-me"), "x");

        Assert.False(LinuxAppPlace.CleanUpOldCopy(LocalDir, newApp));
        Assert.True(File.Exists(Path.Combine(AppDir, "keep-me")));
        Assert.False(File.Exists(LinuxAppPlace.MarkerPath(LocalDir)));
    }
}
