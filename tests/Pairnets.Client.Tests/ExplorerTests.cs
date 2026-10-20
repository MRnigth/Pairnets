using System.Windows;
using Microsoft.Win32;
using Pairnets.Client.Platform;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Client.Tests;

/// <summary>
/// Pairnets in File Explorer: the left-side entry, the right-click items and the folder icon. The registry part is
/// written below a throwaway key (never the real HKEY_CURRENT_USER\Software\Classes, so Explorer on this PC never sees
/// it), and the right-click requests go through the real tray app with Windows stood in for.
/// </summary>
public sealed class ExplorerTests : IDisposable
{
    private readonly string _testKey = @"Software\Pairnets-Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _software;
    private readonly TempDir _dir = new("explorer");

    public ExplorerTests() => _software = Registry.CurrentUser.CreateSubKey(_testKey);

    public void Dispose()
    {
        ExplorerShell.RemoveFolderIcon(_dir.Path);
        _software.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        _dir.Dispose();
    }

    private ExplorerEntry Entry(string folder) =>
        new(folder, @"C:\Program Files\Pairnets\Pairnets.exe", _dir.Combine("Pairnets", "AppIcon.ico"), @"C:\Icons\FolderIcon.ico");

    private string? Value(string key, string name = "")
    {
        using var k = _software.OpenSubKey(key);
        return k?.GetValue(name)?.ToString();
    }

    [Fact]
    public void Apply_PutsPairnetsInTheLeftSide_PointingAtTheFolder()
    {
        var folder = _dir.Combine("My Pairnets");
        Directory.CreateDirectory(folder);

        Assert.True(ExplorerShell.Apply(_software, Entry(folder)));

        var clsid = @"Classes\CLSID\" + ExplorerShell.FolderClsid;
        Assert.Equal("Pairnets", Value(clsid));
        Assert.Equal("1", Value(clsid, "System.IsPinnedToNameSpaceTree"));
        Assert.Equal(Entry(folder).AppIcon + ",0", Value(clsid + @"\DefaultIcon"));
        Assert.Equal("{0E5AAE11-A475-4c5b-AB00-C66DE400274E}", Value(clsid + @"\Instance", "CLSID"));
        Assert.Equal(folder, Value(clsid + @"\Instance\InitPropertyBag", "TargetFolderPath"));
        Assert.Equal(folder, ExplorerShell.RegisteredFolder(_software));
        Assert.Equal("Pairnets", Value(@"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + ExplorerShell.FolderClsid));
        Assert.Equal("1", Value(@"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", ExplorerShell.FolderClsid));
    }

    [Theory]
    [InlineData(@"Classes\*\shell\Pairnets.Open", "Open in Pairnets", "--open-in-pairnets")]
    [InlineData(@"Classes\*\shell\Pairnets.Status", "Show sync status", "--sync-status")]
    [InlineData(@"Classes\Directory\shell\Pairnets.Open", "Open in Pairnets", "--open-in-pairnets")]
    [InlineData(@"Classes\Directory\shell\Pairnets.Status", "Show sync status", "--sync-status")]
    [InlineData(@"Classes\Directory\shell\Pairnets.Where", "Where this folder lives…", "--where-it-lives")]
    public void Apply_AddsRightClickItems_OnlyInsideThePairnetsFolder(string key, string text, string argument)
    {
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);

        ExplorerShell.Apply(_software, Entry(folder));

        Assert.Equal(text, Value(key, "MUIVerb"));
        Assert.Equal($"System.ItemPathDisplay:~<\"{folder}\\\" OR System.ItemPathDisplay:=\"{folder}\"", Value(key, "AppliesTo"));
        Assert.Equal("Single", Value(key, "MultiSelectModel")); // one Pairnets for a whole selection
        Assert.Equal($"\"C:\\Program Files\\Pairnets\\Pairnets.exe\" {argument} \"%1\"", Value(key + @"\command"));
        Assert.Equal(FileManagerCommand.FromArguments([argument, folder + @"\a.txt"])!.Path, folder + @"\a.txt");
    }

    [Fact]
    public void Apply_WithoutRightClickItems_KeepsTheRest_AndStillKnowsTheFolder()
    {
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);
        ExplorerShell.Apply(_software, Entry(folder));

        ExplorerShell.Apply(_software, Entry(folder), rightClick: false);

        foreach (var verb in new[] { @"Classes\*\shell\Pairnets.Open", @"Classes\Directory\shell\Pairnets.Status", @"Classes\Directory\shell\Pairnets.Where" })
            Assert.Null(_software.OpenSubKey(verb));
        Assert.Equal("Pairnets", Value(@"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + ExplorerShell.FolderClsid));
        Assert.Equal(folder, ExplorerShell.RegisteredFolder(_software));
        ExplorerShell.Remove(_software, folder);
        Assert.Null(ExplorerShell.RegisteredFolder(_software));
    }

    [Fact]
    public void Remove_TakesEverythingAwayAgain_AndIsSafeToRepeat()
    {
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);
        using (var hide = _software.CreateSubKey(@"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
            hide.SetValue("{someone-else}", 1); // another program's value next to ours stays
        ExplorerShell.Apply(_software, Entry(folder));

        ExplorerShell.Remove(_software, folder);
        ExplorerShell.Remove(_software, folder);

        Assert.Null(_software.OpenSubKey(@"Classes\CLSID\" + ExplorerShell.FolderClsid));
        Assert.Null(_software.OpenSubKey(@"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + ExplorerShell.FolderClsid));
        Assert.Null(Value(@"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", ExplorerShell.FolderClsid));
        Assert.Equal("1", Value(@"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{someone-else}"));
        foreach (var verb in new[] { @"Classes\*\shell\Pairnets.Open", @"Classes\*\shell\Pairnets.Status", @"Classes\Directory\shell\Pairnets.Open", @"Classes\Directory\shell\Pairnets.Status", @"Classes\Directory\shell\Pairnets.Where" })
            Assert.Null(_software.OpenSubKey(verb));
        Assert.Null(_software.OpenSubKey(@"Classes\*\shell\Pairnets.Where")); // folders only
        Assert.Null(ExplorerShell.RegisteredFolder(_software));
        Assert.False(File.Exists(Path.Combine(folder, "desktop.ini")));
    }

    [Fact]
    public void FolderIcon_IsAHiddenDesktopIni_ThatPairnetsNeverSyncs()
    {
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");

        Assert.True(ExplorerShell.SetFolderIcon(folder, @"C:\Icons\FolderIcon.ico"));
        Assert.True(ExplorerShell.SetFolderIcon(folder, @"C:\Icons\FolderIcon.ico")); // again: nothing changes

        var ini = Path.Combine(folder, "desktop.ini");
        Assert.Contains(@"IconResource=C:\Icons\FolderIcon.ico,0", File.ReadAllText(ini));
        Assert.Equal(FileAttributes.Hidden | FileAttributes.System, File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.True((new DirectoryInfo(folder).Attributes & FileAttributes.ReadOnly) != 0);
        Assert.True(new IgnoreList().IsIgnored("desktop.ini"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "notes.txt")));

        ExplorerShell.RemoveFolderIcon(folder);
        Assert.False(File.Exists(ini));
        Assert.Equal(0, (int)(new DirectoryInfo(folder).Attributes & FileAttributes.ReadOnly));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "notes.txt")));
    }

    [Fact]
    public void FolderIcon_LeavesSomeoneElsesDesktopIniAlone()
    {
        var folder = _dir.Combine("Pairnets");
        Directory.CreateDirectory(folder);
        var ini = Path.Combine(folder, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=C:\\mine.ico,0\r\n");

        Assert.False(ExplorerShell.SetFolderIcon(folder, @"C:\Icons\FolderIcon.ico"));
        ExplorerShell.RemoveFolderIcon(folder);

        Assert.Equal("[.ShellClassInfo]\r\nIconResource=C:\\mine.ico,0\r\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task FileManagerCommand_TravelsToTheRunningApp()
    {
        var pipe = "pairnets-test-" + Guid.NewGuid().ToString("N")[..8];
        var heard = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plain = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = AppActivation.Listen(message => (heard.Task.IsCompleted ? plain : heard).TrySetResult(message), pipe);
        var command = new FileManagerCommand(FileManagerAction.ShowStatus, @"D:\Pairnets\Photos\Beach ø.jpg");

        Assert.True(SpinWait.SpinUntil(() => AppActivation.TrySendToRunning(command.ToMessage(), pipe, 200), TimeSpan.FromSeconds(10)));
        Assert.Equal(command, FileManagerCommand.FromMessage(await heard.Task.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.True(AppActivation.TrySignalRunning(pipe)); // a plain poke still means "come to the front"
        Assert.Null(await plain.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------ the tray app

    [Fact]
    public async Task App_PutsItsFolderInExplorer_AndTakesItAwayWhenSwitchedOff()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        await app.StartAsync();
        Assert.Equal([app.Folder], app.Platform.ExplorerEntries.Items);

        await app.Menu("Settings…");
        var main = app.Tray.Window!;
        await app.WaitUntil(() => main.SettingsPage.Content is Ui.SettingsView, "the Settings page");
        await app.Wpf.Ui(() => ((Ui.SettingsView)main.SettingsPage.Content).ExplorerBox.IsChecked = false);
        await app.Press(main, "Save");
        await app.WaitUntil(() => app.Platform.ExplorerEntries.Items.Count == 2, "Pairnets to leave Explorer's left side");
        // The right-click items are a switch of their own, still on: only the left side and the folder icon go.
        Assert.Equal(app.Folder, app.Platform.ExplorerEntries.Items[^1]);
        Assert.Equal((false, true), app.Platform.ExplorerParts.Items[^1]);
        Assert.False(app.SavedSettings.ShowInFileManager);
    }

    [Fact]
    public async Task App_TakesTheInstallersChoicesOnce()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest, setupChoices: ("run-1", "explorerside=0;explorermenu=0;where=nest;autostart=0"));
        await app.StartAsync();

        Assert.Null(app.Platform.ExplorerEntries.Items[^1]); // neither part wanted
        Assert.Equal(Core.Sync.FilePlacement.NestOnly, app.SavedSettings.Placement[""]);
        Assert.Equal("run-1", app.SavedSettings.SetupChoicesApplied);
    }

    [Fact]
    public async Task App_LeavesExplorerWhenReset()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        await app.StartAsync();

        await app.Menu("Settings…");
        app.Dialogs.When("Reset Pairnets on this computer?", MessageBoxResult.OK);
        await app.Press(app.Tray.Window!, "Reset this app…");
        await app.Showing<Ui.SettingsWindow>("the sign-in window after the reset");

        Assert.Null(app.Platform.ExplorerEntries.Items[^1]);
    }

    [Fact]
    public async Task ShowSyncStatus_FromExplorer_SaysWhereTheFileStands()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest, watchFolder: false);
        File.WriteAllText(Path.Combine(app.Folder, "Budget.xlsx"), "numbers");
        await app.StartAsync();
        await app.SyncedOnce();
        await app.WaitUntil(() => app.Tray.Session!.GetFileStatus(Path.Combine(app.Folder, "Budget.xlsx")).State == FileSyncState.Synced, "the file to count as synced");
        File.WriteAllText(Path.Combine(app.Folder, "New idea.txt"), "not synced yet");

        // Through the pipe, as a right-click on a running Pairnets does it.
        var synced = new FileManagerCommand(FileManagerAction.ShowStatus, Path.Combine(app.Folder, "Budget.xlsx"));
        Assert.True(AppActivation.TrySendToRunning(synced.ToMessage(), app.Env.ActivationPipeName));
        await app.WaitUntil(() => app.Dialogs.Asked.Items.Any(q => q.Title == "Pairnets – sync status"), "the status answer");
        Assert.Equal("Budget.xlsx\n\nSynced: the same here and on your nest.", app.Dialogs.Asked.Items.Last(q => q.Title == "Pairnets – sync status").Text);

        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.ShowStatus, Path.Combine(app.Folder, "New idea.txt"))));
        Assert.Equal("New idea.txt\n\nNew here: the next sync uploads it.", app.Dialogs.Asked.Items.Last(q => q.Title == "Pairnets – sync status").Text);

        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.ShowStatus, app.Folder)));
        Assert.Equal("Your Pairnets folder\n\nYour Pairnets folder is up to date.", app.Dialogs.Asked.Items.Last(q => q.Title == "Pairnets – sync status").Text);
    }

    [Fact]
    public async Task OpenInPairnets_FromExplorer_ShowsTheFilesHistory()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        Directory.CreateDirectory(Path.Combine(app.Folder, "Photos"));
        File.WriteAllText(Path.Combine(app.Folder, "Photos", "Beach.jpg"), "sand");
        await app.StartAsync();
        // (a file written just now waits a moment before it is sent, so wait for the nest itself)
        await app.WaitUntil(() => nest.Store.ReadManifest(null).Entries.Any(e => e.Path == "Photos/Beach.jpg" && !e.Deleted), "the file to reach the nest");

        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.Open, Path.Combine(app.Folder, "Photos", "Beach.jpg"))));
        await app.WaitUntil(() => app.Tray.Window!.Page == Ui.MainPage.History, "the History page");
        var history = app.Tray.Window!.HistoryPane;
        await app.WaitUntil(() => history.DetailName.Text == "Beach.jpg", "the file chosen on the History page");

        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.Open, Path.Combine(app.Folder, "Photos"))));
        Assert.Equal("Photos", await app.Wpf.Ui(() => app.Tray.Window!.HistoryPane.SearchText));

        // Outside the Pairnets folder: just the window, nothing is looked up.
        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.Open, @"C:\Windows\notepad.exe")));
        Assert.True(await app.Wpf.Ui(() => app.Tray.Window!.IsVisible));
    }
    [Fact]
    public async Task WhereThisFolderLives_FromExplorer_OpensTheFilesPageOnThatFolderWithItsChoices()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        Directory.CreateDirectory(Path.Combine(app.Folder, "Photos"));
        File.WriteAllText(Path.Combine(app.Folder, "Photos", "Beach.jpg"), "sand");
        await app.StartAsync();
        await app.WaitUntil(() => nest.Store.ReadManifest(null).Entries.Any(e => e.Path == "Photos/Beach.jpg" && !e.Deleted), "the file to reach the nest");
        await app.WaitUntil(() => app.Tray.Session?.Files.List("Photos").Count == 1, "this computer to know the nest's list");

        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.WhereItLives, Path.Combine(app.Folder, "Photos"))));
        await app.WaitUntil(() => app.Tray.Window!.Page == Ui.MainPage.Files, "the Files page");
        var files = app.Tray.Window!.FilesPane;
        Assert.Equal("Photos", await app.Wpf.Ui(() => files.Folder));
        await app.WaitUntil(() => files.PlacementMenuOpen, "the choices of where Photos lives");
        Assert.Equal("On this PC", await app.Wpf.Ui(() => files.PlacementShown));
        await app.Wpf.Ui(() => files.PlacementMenu.IsOpen = false);

        // A file: the folder it is in.
        await app.Wpf.Ui(() => app.Tray.HandleFileCommand(new FileManagerCommand(FileManagerAction.WhereItLives, Path.Combine(app.Folder, "Photos", "Beach.jpg"))));
        Assert.Equal("Photos", await app.Wpf.Ui(() => files.Folder));
    }
}
