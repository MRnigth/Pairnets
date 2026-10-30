using System.Windows;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Client.Tests;

/// <summary>
/// The Files page ("All files" and "Deleted"), Clean up and the pop-ups of "Where your files live" in the Windows app,
/// with the nest's files in memory. The click-everything tests press every button; these check what the pages show and
/// what a press changes.
/// </summary>
public sealed class FilesPageTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static readonly ServerInfo Server = new("id", 1, 1, PairnetsInfo.ProductVersion, 3L << 30, 1L << 40);

    private static async Task<(MainWindow Window, RecordingActions Actions, FakeNestFiles Nest, DialogScript Script, IDisposable Answers)> OpenAsync()
    {
        var actions = new RecordingActions(new SampleHistory());
        var nest = actions.NestFiles!;
        var script = new DialogScript();
        var answers = script.Install();
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(actions) { Width = 1100, Height = 760 };
            w.Show();
            w.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", Server = Server }, @"D:\Sync", "DESKTOP");
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        return (window, actions, nest, script, answers);
    }

    private static async Task Done(IDisposable answers)
    {
        await Wpf.Settle();
        Assert.Empty(Wpf.TakeCrashes());
        await Wpf.CloseAllWindows();
        answers.Dispose();
    }

    [Fact]
    public async Task TheFilesTile_ShowsTheNestsFoldersAndFiles_AndAFolderOpensOnItsRow()
    {
        var (window, actions, nest, _, answers) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        var page = window.FilesPane;
        Assert.True(await Wpf.Ui(() => window.NavFiles.IsChecked == true && page.IsVisible && !window.HistoryPane.IsVisible));
        Assert.Equal(["Documents", "Photos", "Videos", "notes.txt"], await Wpf.Ui(() => page.Rows.Select(r => r.Name).ToList()));
        Assert.Equal(["Your nest"], await Wpf.Ui(() => page.CrumbTexts));
        Assert.Equal("Only on the nest", await Wpf.Ui(() => page.Rows.Single(r => r.Name == "Photos").WhereText));
        Assert.Equal(FilesPageText.Footer(nest.BytesOnNest(), nest.BytesHere(string.Empty)), await Wpf.Ui(() => page.FooterLine));

        await Wpf.Ui(() => page.OpenAsync(page.Rows.Single(r => r.Name == "Documents")));
        Assert.Equal("Documents", await Wpf.Ui(() => page.Folder));
        Assert.Equal(["Your nest", "Documents"], await Wpf.Ui(() => page.CrumbTexts));
        Assert.Equal(["taxes", "report.docx"], await Wpf.Ui(() => page.Rows.Select(r => r.Name).ToList()));

        // A file opens in its program: the copy the nest's files say.
        await Wpf.Ui(() => page.OpenAsync(page.Rows.Single(r => r.Name == "report.docx")));
        Assert.Contains($"OpenFile({nest.OpenedPath})", actions.Calls.Items);
        await Done(answers);
    }

    [Fact]
    public async Task TheDeletedTab_IsTheHistoryPage_AndShowsHowManyWereDeleted()
    {
        var (window, _, _, _, answers) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        await Wpf.WaitUntil(() => window.DeletedTabCount.Text == "3", "the Deleted tab's count");
        await Wpf.Ui(() => window.DeletedFilesTab.IsChecked = true);
        Assert.Equal(MainPage.History, window.Page);
        await Wpf.WaitUntil(() => window.HistoryPane.DeletedRows.Count == 3, "the deleted files");
        Assert.True(await Wpf.Ui(() => window.HistoryPane.IsVisible && !window.FilesPane.IsVisible && window.NavFiles.IsChecked == true));
        await Done(answers);
    }

    [Fact]
    public async Task WhileTheNestCantBeReached_TheBannerShowsAndNothingCanBeAdded()
    {
        var (window, _, nest, _, answers) = await OpenAsync();
        nest.NestOffline = true;
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        Assert.True(await Wpf.Ui(() => window.FilesPane.OfflineShown && !window.FilesPane.AddFilesButton.IsEnabled));
        Assert.Equal(4, await Wpf.Ui(() => window.FilesPane.Rows.Count)); // the last list seen still shows
        await Done(answers);
    }

    [Fact]
    public async Task KeepingAFolderOnlyOnTheNest_AsksFirst_AndSavesOnlyOnYes()
    {
        var (window, _, nest, script, answers) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        Assert.False(await Wpf.Ui(() => window.FilesPane.ChoosePlacement("Documents", FilePlacement.NestOnly)));
        var asked = script.Popups.Items.Single();
        Assert.Equal("Keep Documents only on your nest?", asked);
        Assert.DoesNotContain(nest.Calls, c => c.StartsWith("place:", StringComparison.Ordinal));

        script.Chooses = choice => choice.Buttons[^1];
        Assert.True(await Wpf.Ui(() => window.FilesPane.ChoosePlacement("Documents", FilePlacement.NestOnly)));
        Assert.Contains("place:Documents:NestOnly", nest.Calls);
        Assert.Equal(FilePlacement.NestOnly, nest.PlacementOf("Documents/report.docx"));

        // Back to this PC needs no question.
        script.Chooses = _ => null;
        Assert.True(await Wpf.Ui(() => window.FilesPane.ChoosePlacement("Photos", FilePlacement.OnThisPc)));
        Assert.Equal(2, script.Popups.Count);
        await Done(answers);
    }

    [Fact]
    public async Task AChangeOnAFullNest_ShowsTheNestFullPopUp_AndHelpMeCleanUpOpensCleanUp()
    {
        var (window, _, nest, script, answers) = await OpenAsync();
        nest.NestFull = true;
        script.Chooses = choice => choice.Title == FilesPageText.FullTitle ? FilesPageText.HelpMeCleanUp : null;
        var file = Path.Combine(Path.GetTempPath(), "pairnets-full-test.txt");
        File.WriteAllText(file, "x");
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        await Wpf.Ui(() => window.FilesPane.AddAsync([file]));
        Assert.Contains(FilesPageText.FullTitle, script.Popups.Items);
        Assert.True(await Wpf.Ui(() => window.CleanupShown && window.CleanupPane.IsVisible));
        Assert.Equal("wedding-full-day.mp4", await Wpf.Ui(() => window.CleanupPane.Rows[0].Name)); // biggest first
        Assert.Contains("3.00 GB free of 1.00 TB", await Wpf.Ui(() => window.CleanupPane.CleanupSubtitle.Text));
        await Done(answers);
    }

    [Fact]
    public async Task DeletingForGood_HappensOnlyWithTheWordTyped()
    {
        var (window, _, nest, script, answers) = await OpenAsync();
        await Wpf.Ui(window.ShowCleanup);
        var biggest = await Wpf.Ui(() => window.CleanupPane.Rows[0].Entry);

        await Wpf.Ui(() => window.CleanupPane.DeleteForGoodAsync(biggest)); // "Keep it"
        Assert.Equal("Delete “wedding-full-day.mp4” for good?", script.Popups.Items.Single());
        Assert.True(nest.Has(biggest.Path));

        script.DeleteWord = NestFiles.DeleteWord;
        await Wpf.Ui(() => window.CleanupPane.DeleteForGoodAsync(biggest));
        Assert.Contains($"purge:{biggest.Path}:DELETE", nest.Calls);
        Assert.False(nest.Has(biggest.Path));
        Assert.DoesNotContain(await Wpf.Ui(() => window.CleanupPane.Rows.Select(r => r.Entry.Path).ToList()), p => p == biggest.Path);
        await Done(answers);
    }

    [Fact]
    public void TheStrongQuestion_WorksOnlyWhenTheBoxIsTickedAndTheWordTyped()
    {
        Assert.False(FilesPageText.SureReady(false, "DELETE"));
        Assert.False(FilesPageText.SureReady(true, "delete"));
        Assert.False(FilesPageText.SureReady(true, ""));
        Assert.True(FilesPageText.SureReady(true, " DELETE "));
    }

    [Fact]
    public async Task TheSettingsCard_ListsTheTopFolders_AsksFirst_AndAppliesTheChoiceAtOnce()
    {
        var nest = new FakeNestFiles();
        var script = new DialogScript { Chooses = choice => choice.Buttons[^1] };
        using var answers = script.Install();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: nest));
        var applied = new List<ClientSettings>();
        await Wpf.Ui(() => view.SwitchesChanged += applied.Add);
        Assert.Equal(["", "Documents", "Photos", "Videos"], await Wpf.Ui(() => view.PlacementFolders.ToList()));
        Assert.Equal(FilePlacement.NestOnly, await Wpf.Ui(() => view.PlacementChosen("Photos")));

        await Wpf.Ui(() => view.ChoosePlacement("Videos", FilePlacement.NestOnly));
        Assert.Equal("Keep Videos only on your nest?", script.Popups.Items.Single());
        Assert.Equal(FilePlacement.NestOnly, await Wpf.Ui(() => view.PlacementChosen("Videos")));
        Assert.Empty(nest.Calls); // through the app's settings, like the switches
        Assert.Equal(FilePlacement.NestOnly, applied.Single().Placement["Videos"]); // at once (A11)
        Assert.Equal(FilePlacement.NestOnly, applied.Single().Placement["Photos"]);

        // Without a nest the card shows, greyed out, with a note.
        var off = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets()));
        Assert.Equal(FilesPageText.SettingsOff, await Wpf.Ui(() => off.PlacementNote.Text));
        Assert.False(await Wpf.Ui(() => off.PlacementRows.IsEnabled));
        Assert.Empty(Wpf.TakeCrashes());
    }

    /// <summary>The choices the card offers on <paramref name="folder"/>'s row, by their names.</summary>
    private static IReadOnlyList<string> Offered(SettingsView view, string folder)
    {
        var row = folder.Length == 0 ? FilesPageText.EverythingElse : folder;
        return Logical(view.PlacementRows).OfType<System.Windows.Controls.RadioButton>()
            .Select(System.Windows.Automation.AutomationProperties.GetName)
            .Where(n => n.StartsWith(row + ": ", StringComparison.Ordinal))
            .Select(n => n[(row.Length + 2)..])
            .ToList();
    }

    private static IEnumerable<object> Logical(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            yield return child;
            if (child is DependencyObject d)
            {
                foreach (var below in Logical(d))
                    yield return below;
            }
        }
    }

    [Fact]
    public async Task TheWholeFolderRow_NeverOffersOnlyOnTheNest_AndNamesWhatAChoiceChanges()
    {
        var nest = new FakeNestFiles();
        var script = new DialogScript { Chooses = choice => choice.Buttons[^1] };
        using var answers = script.Install();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: nest));
        var applied = new List<ClientSettings>();
        await Wpf.Ui(() => view.SwitchesChanged += applied.Add);

        Assert.DoesNotContain("Only on the nest", await Wpf.Ui(() => Offered(view, "")));
        Assert.Contains("On this PC", await Wpf.Ui(() => Offered(view, "")));
        Assert.Contains("Only on the nest", await Wpf.Ui(() => Offered(view, "Videos")));

        // Asked for anyway: nothing is asked, saved or applied.
        await Wpf.Ui(() => view.ChoosePlacement("", FilePlacement.NestOnly));
        Assert.Empty(script.Popups.Items);
        Assert.Empty(applied);
        Assert.Equal(FilePlacement.OnThisPc, await Wpf.Ui(() => view.PlacementChosen("")));
        Assert.Null(await Wpf.Ui(() => view.PlacementStatusShown));

        // On-Demand for the whole folder names the loose files and every folder without its own choice first.
        await Wpf.Ui(() => view.ChoosePlacement("", FilePlacement.OnDemand));
        var asked = script.Choices.Items.Single();
        Assert.Equal("This changes: 1 file right in your Pairnets folder (not in a folder), Documents (2 files), Videos (1 file).", asked.Points[0]);
        Assert.Equal(FilePlacement.OnDemand, applied.Single().Placement[""]);
        Assert.Equal("Saved: All other files → On-Demand.", await Wpf.Ui(() => view.PlacementStatusShown));
        Assert.Empty(Wpf.TakeCrashes());
    }

    [Fact]
    public async Task AChoice_StartsTheSyncAgain_WithoutACrash_AndTheCardFollowsTheDownload()
    {
        var first = new FakeNestFiles();
        var running = (INestFiles)first;
        var script = new DialogScript { Chooses = choice => choice.Buttons[^1] };
        using var answers = script.Install();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        settings.Placement["Videos"] = FilePlacement.NestOnly;
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: first) { FilesSource = () => running });
        await Wpf.Ui(() => view.SwitchesChanged += _ =>
        {
            // As the app does: the old sync's list is closed and a new one runs.
            first.Closed = true;
            var next = new FakeNestFiles();
            next.Placement.Clear();
            running = next;
        });

        await Wpf.Ui(() => view.ChoosePlacement("Videos", FilePlacement.OnThisPc));
        Assert.Empty(Wpf.TakeCrashes());
        Assert.Equal(FilePlacement.OnThisPc, await Wpf.Ui(() => view.PlacementChosen("Videos")));
        Assert.Equal("Saved: Videos → On this PC.", await Wpf.Ui(() => view.PlacementStatusShown));
        Assert.Contains("Videos", await Wpf.Ui(() => view.PlacementFolders.ToList()));

        var downloading = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", CurrentPath = "Videos/wedding-full-day.mp4", Operation = "download",
            FilesTotal = 149, FilesDone = 37, Active = [new ActiveTransfer("Videos/wedding-full-day.mp4", "download", 10, 100)],
        };
        await Wpf.Ui(() => view.ShowPlacementStatus(downloading));
        var line = await Wpf.Ui(() => view.PlacementStatusShown);
        Assert.StartsWith("Saved: Videos → On this PC. Downloading", line);
        Assert.Contains("37 of 149 files", line);

        // Even a page with no new list to read draws again without a crash.
        var stale = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: first));
        await Wpf.Ui(() => stale.ChoosePlacement("Documents", FilePlacement.OnThisPc));
        Assert.Empty(Wpf.TakeCrashes());
    }

    [Fact]
    public async Task AFolderFreedAfterItsChoice_NoLongerSaysAllOnThisPc()
    {
        var nest = new FakeNestFiles();
        var script = new DialogScript { Chooses = choice => choice.Buttons[^1] };
        using var answers = script.Install();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: nest) { FilesSource = () => nest });
        await view.PlacementSizesReady;
        Assert.EndsWith("all on this PC", await Wpf.Ui(() => view.PlacementSizeShown("Documents")));

        await Wpf.Ui(() => view.ChoosePlacement("Documents", FilePlacement.NestOnly));
        // The sync frees the files a moment later; the next status the app shows reads the line again.
        var now = DateTimeOffset.Now;
        nest.Put("Documents/report.docx", 2_400_000, now, here: false);
        nest.Put("Documents/taxes/2025.pdf", 800_000, now, here: false);
        await Wpf.Ui(() => view.ShowPlacementStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" }));
        await view.PlacementSizesReady;
        Assert.Equal(FilesPageText.SizeLine(nest.List(string.Empty).Single(e => e.Path == "Documents").Size, 0),
            await Wpf.Ui(() => view.PlacementSizeShown("Documents")));
        Assert.Empty(Wpf.TakeCrashes());
    }

    [Fact]
    public async Task OpeningSettings_DoesNotWaitForTheFilesOnThisPcToBeCounted()
    {
        // The window froze 20-30 s when it counted, folder by folder, every file on this PC before it showed Settings.
        var nest = new FakeNestFiles();
        using var slowDisk = new ManualResetEventSlim(false);
        nest.HoldBytes = slowDisk;
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: nest) { FilesSource = () => nest });
        var opened = watch.Elapsed;
        Assert.True(opened < TimeSpan.FromSeconds(1.5), $"Settings took {opened.TotalSeconds:0.0} s to open while the disk was slow");
        Assert.Equal(FilesPageText.CountingSize, await Wpf.Ui(() => view.PlacementSizeShown("Documents")));

        slowDisk.Set();
        await view.PlacementSizesReady;
        Assert.EndsWith("all on this PC", await Wpf.Ui(() => view.PlacementSizeShown("Documents")));
        Assert.Equal(1, nest.BytesReads); // one pass for every folder, not one per folder
        Assert.Empty(Wpf.TakeCrashes());
    }

    [Fact]
    public async Task AFolderRowsMenu_OpensWhereThisFolderLives_WithItsThreeChoices()
    {
        var (window, _, _, _, answers) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        await Wpf.Settle();
        var shown = await Wpf.Ui(() =>
        {
            var button = Presser.Elements(window).OfType<System.Windows.Controls.Button>()
                .First(b => b.DataContext is NestRow { IsFolder: true } && b.ContextMenu is not null);
            var menu = button.ContextMenu!;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            menu.UpdateLayout();
            var where = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => (string)i.Header == "Where this folder lives");
            where.IsSubmenuOpen = true;
            where.UpdateLayout();
            var popup = (System.Windows.Controls.Primitives.Popup)where.Template.FindName("PART_Popup", where);
            popup.Child.UpdateLayout();
            var choices = where.Items.OfType<System.Windows.Controls.MenuItem>().Where(i => i.IsVisible).Select(i => (string)i.Header).ToList();
            var open = popup.IsOpen;
            menu.IsOpen = false;
            return (open, choices);
        });
        Assert.True(shown.open);
        Assert.Equal(["On this PC", "On-Demand", "Only on the nest"], shown.choices);
        await Done(answers);
    }

    [Fact]
    public async Task TheFilesPage_ChoosesForExactlyTheFolderPicked_NeverTheWholeFolder()
    {
        var (window, _, nest, script, answers) = await OpenAsync();
        script.Chooses = choice => choice.Buttons[^1];
        await Wpf.Ui(() => window.Navigate(MainPage.Files));

        // At the top of the nest (the whole folder) nest only is refused without a question.
        Assert.False(await Wpf.Ui(() => window.FilesPane.ChoosePlacement("", FilePlacement.NestOnly)));
        Assert.Empty(script.Popups.Items);

        Assert.True(await Wpf.Ui(() => window.FilesPane.ChoosePlacement("Videos", FilePlacement.NestOnly)));
        Assert.Equal(["place:Videos:NestOnly"], nest.Calls.Where(c => c.StartsWith("place:", StringComparison.Ordinal)));
        Assert.DoesNotContain("", nest.Placement.Keys);
        Assert.Equal("This changes: 1 file right in Videos.", script.Choices.Items.Single().Points[0]);
        await Done(answers);
    }
}
