using System.Xml.Linq;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Desktop.Platform;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Pairnets in the file manager, the parts shared by every computer: the right-click requests, a file's sync status,
/// and the Linux sidebar bookmark, right-click items and folder icon (written into a temporary home folder).
/// </summary>
public sealed class FileManagerTests : IDisposable
{
    private readonly TempDir _dir = new("filemanager");

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ right-click requests

    [Theory]
    [InlineData(FileManagerCommand.OpenArgument, FileManagerAction.Open)]
    [InlineData(FileManagerCommand.StatusArgument, FileManagerAction.ShowStatus)]
    public void Command_ComesFromTheProgramsArguments_AndTravelsAsAMessage(string argument, FileManagerAction action)
    {
        var path = Path.Combine(_dir.Path, "Photos", "Beach ø.jpg");
        var command = FileManagerCommand.FromArguments(["--autostart", argument, path]);

        Assert.Equal(new FileManagerCommand(action, path), command);
        Assert.Equal(command, FileManagerCommand.FromMessage(command!.ToMessage()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pairnets-file:open")]
    [InlineData("pairnets-file:open\n")]
    [InlineData("pairnets-file:delete\n/data/example/x")]
    [InlineData("pairnets-file:open\n/data/example/x\0y")]
    [InlineData("something else")]
    public void Command_OnlyAWellFormedMessageCounts(string? message) => Assert.Null(FileManagerCommand.FromMessage(message));

    [Fact]
    public void Command_NeedsAPathAfterTheArgument() =>
        Assert.Null(FileManagerCommand.FromArguments([FileManagerCommand.OpenArgument]));

    // ------------------------------------------------------------------ sync status

    private static readonly StatusSnapshot Quiet = StatusSnapshot.Initial;

    private (string Root, StateDb State) Folder()
    {
        var root = _dir.Combine("Pairnets");
        Directory.CreateDirectory(root);
        return (root, new StateDb(_dir.Combine("state.db")));
    }

    /// <summary>A file Pairnets synced: the notes say what it was, the nest has the same.</summary>
    private static string Synced(string root, StateDb state, string rel, string text)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        var info = new FileInfo(full);
        state.SetSynced(rel, "hash-" + text, info.Length, info.LastWriteTimeUtc.Ticks, "hash-" + text, info.LastWriteTimeUtc.Ticks);
        state.UpsertRemote(new ManifestEntry(rel, "hash-" + text, info.Length, 0, false, 1));
        return full;
    }

    [Fact]
    public void Status_Synced_WhenTheFileIsWhatWasLastSynced()
    {
        var (root, state) = Folder();
        using (state)
        {
            var full = Synced(root, state, "Budget.xlsx", "numbers");
            var status = FileStatusReader.For(root, full, state, new IgnoreList(), Quiet);
            Assert.Equal((FileSyncState.Synced, "Budget.xlsx"), (status.State, status.SyncPath));
        }
    }

    [Fact]
    public void Status_Waiting_WhenChangedHere_OnTheNest_OrNew()
    {
        var (root, state) = Folder();
        using (state)
        {
            var mine = Synced(root, state, "mine.txt", "one");
            File.AppendAllText(mine, " and more");
            var theirs = Synced(root, state, "theirs.txt", "one");
            state.UpsertRemote(new ManifestEntry("theirs.txt", "hash-two", 3, 0, false, 2));
            var fresh = Path.Combine(root, "fresh.txt");
            File.WriteAllText(fresh, "new");

            Assert.Equal("Changed here: the next sync uploads it.", FileStatusReader.For(root, mine, state, new IgnoreList(), Quiet).Text);
            Assert.Equal("Changed on another computer: the next sync brings it here.", FileStatusReader.For(root, theirs, state, new IgnoreList(), Quiet).Text);
            Assert.Equal("New here: the next sync uploads it.", FileStatusReader.For(root, fresh, state, new IgnoreList(), Quiet).Text);
            Assert.All(new[] { mine, theirs, fresh }, f => Assert.Equal(FileSyncState.Waiting, FileStatusReader.For(root, f, state, new IgnoreList(), Quiet).State));
        }
    }

    [Fact]
    public void Status_Syncing_WhileMovingOrNextInTheBatch()
    {
        var (root, state) = Folder();
        using (state)
        {
            var big = Synced(root, state, "Videos/Holiday.mp4", "frames");
            var next = Synced(root, state, "Videos/Beach.mp4", "waves");
            var status = Quiet with
            {
                Active = [new ActiveTransfer("Videos/Holiday.mp4", "download", 62, 100)],
                Batch = [new BatchFile("Videos/Holiday.mp4", "download", BatchFileState.Moving, 62), new BatchFile("Videos/Beach.mp4", "upload", BatchFileState.Waiting)],
            };

            Assert.Equal("Downloading · 62 %", FileStatusReader.For(root, big, state, new IgnoreList(), status).Text);
            Assert.Equal("Waiting to upload", FileStatusReader.For(root, next, state, new IgnoreList(), status).Text);
            var folder = FileStatusReader.For(root, Path.Combine(root, "Videos"), state, new IgnoreList(), status);
            Assert.Equal((FileSyncState.Syncing, "Syncing 2 files…", true), (folder.State, folder.Text, folder.IsFolder));
        }
    }

    [Fact]
    public void Status_Problem_FromAPathWarning_AlsoOnTheFolderAbove()
    {
        var (root, state) = Folder();
        using (state)
        {
            var full = Synced(root, state, "Docs/report.docx", "text");
            state.SetWarning(new PathWarning("Docs/report.docx", ErrorCodes.CaseCollision, "Another file has the same name in other letters.", null, 0, 0));

            var file = FileStatusReader.For(root, full, state, new IgnoreList(), Quiet);
            Assert.Equal((FileSyncState.Problem, "Can't sync: Another file has the same name in other letters."), (file.State, file.Text));
            var top = FileStatusReader.For(root, root, state, new IgnoreList(), Quiet);
            Assert.Equal((FileSyncState.Problem, "Your Pairnets folder has 1 file Pairnets can't sync. Open Pairnets to see why."), (top.State, top.Text));
        }
    }

    [Fact]
    public void Status_IgnoredAndOutside()
    {
        var (root, state) = Folder();
        using (state)
        {
            File.WriteAllText(Path.Combine(root, "desktop.ini"), "[.ShellClassInfo]");
            File.WriteAllText(Path.Combine(root, PathRules.MarkerFileName), "id");

            Assert.Equal(FileSyncState.Ignored, FileStatusReader.For(root, Path.Combine(root, "desktop.ini"), state, new IgnoreList(), Quiet).State);
            Assert.Equal(FileSyncState.Ignored, FileStatusReader.For(root, Path.Combine(root, PathRules.MarkerFileName), state, new IgnoreList(), Quiet).State);
            Assert.Equal(FileSyncState.Unknown, FileStatusReader.For(root, _dir.Combine("elsewhere.txt"), state, new IgnoreList(), Quiet).State);
            Assert.Equal(FileSyncState.Unknown, FileStatusReader.For(root, root + "-copy", state, new IgnoreList(), Quiet).State); // a sibling with the same start
        }
    }

    [Fact]
    public void FinderFolderIconFile_IsNeverSynced_ButAFileCalledIconIs()
    {
        Assert.True(new IgnoreList().IsIgnored("Photos/Icon\r"));
        Assert.False(new IgnoreList().IsIgnored("Photos/Icon"));
        Assert.False(new IgnoreList().IsIgnored("Icon"));
    }

    // ------------------------------------------------------------------ Linux

    private (LinuxFileManager Files, List<string[]> Gio, string Home) Linux()
    {
        var home = _dir.Combine("home");
        Directory.CreateDirectory(home);
        var gio = new List<string[]>();
        return (new LinuxFileManager(home, args =>
        {
            gio.Add(args);
            return 0;
        }), gio, home);
    }

    [Fact]
    public void Linux_AddsASidebarBookmark_KeepingThePersonsOwn()
    {
        var (files, _, _) = Linux();
        var folder = _dir.Combine("My Pairnets");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(files.GtkBookmarks)!);
        File.WriteAllLines(files.GtkBookmarks, ["file:///data/example/Music Music", "file:///data/example/Work"]);

        Assert.True(files.Apply(folder, "/opt/pairnets/Pairnets", "/tmp/icon.png"));
        Assert.True(files.Apply(folder, "/opt/pairnets/Pairnets", "/tmp/icon.png")); // again: still one bookmark

        var lines = File.ReadAllLines(files.GtkBookmarks);
        Assert.Equal(["file:///data/example/Music Music", "file:///data/example/Work", new Uri(folder).AbsoluteUri + " Pairnets"], lines);

        Assert.True(files.Remove(folder));
        Assert.Equal(["file:///data/example/Music Music", "file:///data/example/Work"], File.ReadAllLines(files.GtkBookmarks));
    }

    [Fact]
    public void Linux_AddsAKdePlace_OnlyWhereKdeKeepsThem_AndOnlyRemovesItsOwn()
    {
        var (files, _, home) = Linux();
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);

        files.Apply(folder, "/opt/pairnets/Pairnets", "/tmp/icon.png");
        Assert.False(File.Exists(files.KdePlaces)); // not a KDE desktop: nothing written

        Directory.CreateDirectory(Path.GetDirectoryName(files.KdePlaces)!);
        File.WriteAllText(files.KdePlaces, """
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel>
             <bookmark href="file:///data/example"><title>Home</title></bookmark>
            </xbel>
            """);
        files.Apply(folder, "/opt/pairnets/Pairnets", "/tmp/icon.png");
        files.Apply(folder, "/opt/pairnets/Pairnets", "/tmp/icon.png");

        var places = XDocument.Load(files.KdePlaces).Root!.Elements("bookmark").ToList();
        Assert.Equal(["Home", "Pairnets"], places.Select(p => p.Element("title")!.Value));
        Assert.Equal(new Uri(folder).AbsoluteUri, places[1].Attribute("href")!.Value);
        Assert.Contains(places[1].Descendants("ID"), id => id.Value == LinuxFileManager.KdePlaceId);

        files.Remove(folder);
        Assert.Equal(["Home"], XDocument.Load(files.KdePlaces).Root!.Elements("bookmark").Select(p => p.Element("title")!.Value));
        Assert.StartsWith(Path.Combine(home, ".local"), files.KdePlaces);
    }

    [Fact]
    public void Linux_AddsRightClickItems_ForDolphinAndGnomeFiles()
    {
        var (files, _, _) = Linux();
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);
        const string exe = "/data/example/My Apps/Pairnets";

        files.Apply(folder, exe, "/tmp/icon.png");

        foreach (var menu in files.ServiceMenus)
        {
            var text = File.ReadAllText(menu);
            Assert.Contains("X-KDE-Submenu=Pairnets", text);
            Assert.Contains($"Exec=\"{exe}\" --open-in-pairnets %f", text);
            Assert.Contains($"Exec=\"{exe}\" --sync-status %f", text);
        }
        foreach (var dir in files.ScriptDirs)
        {
            Assert.EndsWith($"exec '{exe}' --open-in-pairnets \"$1\"\n", File.ReadAllText(Path.Combine(dir, "Open in Pairnets")));
            Assert.EndsWith($"exec '{exe}' --sync-status \"$1\"\n", File.ReadAllText(Path.Combine(dir, "Show Pairnets sync status")));
            if (!OperatingSystem.IsWindows())
                Assert.True((File.GetUnixFileMode(Path.Combine(dir, "Open in Pairnets")) & UnixFileMode.UserExecute) != 0);
        }

        files.Remove(folder);
        Assert.All(files.ServiceMenus, m => Assert.False(File.Exists(m)));
        Assert.All(files.ScriptDirs, d => Assert.Empty(Directory.GetFiles(d)));
    }

    [Fact]
    public void Linux_GivesTheFolderItsIcon_AndTakesItAway()
    {
        var (files, gio, _) = Linux();
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);

        files.Apply(folder, "/opt/pairnets/Pairnets", _dir.Combine("pairnets-folder.png"));
        files.Remove(folder);

        Assert.Equal(["set", folder, "metadata::custom-icon", new Uri(_dir.Combine("pairnets-folder.png")).AbsoluteUri], gio[0]);
        Assert.Equal(["set", "-t", "unset", folder, "metadata::custom-icon"], gio[1]);
    }

    [Theory]
    [InlineData("/opt/Pairnets", "\"/opt/Pairnets\"", "'/opt/Pairnets'")]
    [InlineData("/data/example/100% \"odd\" $dir`/it's", "\"/data/example/100%% \\\"odd\\\" \\$dir\\`/it's\"", "'/data/example/100% \"odd\" $dir`/it'\\''s'")]
    public void Linux_ProgramPathsAreQuoted(string path, string desktop, string shell)
    {
        Assert.Equal(desktop, LinuxFileManager.DesktopQuote(path));
        Assert.Equal(shell, LinuxFileManager.ShellQuote(path));
    }
}
