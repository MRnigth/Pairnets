using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The Files page ("All files" and "Deleted"), Clean up, the pop-ups of "Where your files live" and the Settings card in
/// the Mac/Linux app, headless, with the nest's files in memory.
/// </summary>
[Collection(AvaloniaUi.Name)]
public class FilesPageUiTests
{
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.60", 3L << 30, 1L << 40);

    private static (MainWindow Window, SampleActions Actions, FakeNestFiles Nest) Open()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions) { Width = 980, Height = 700 };
        window.Show();
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", Server = Server }, "/Users/me/Work", "MacBook");
        return (window, actions, actions.NestFiles!);
    }

    /// <summary>The pop-up window the page opened (not the main window), by the start of its title.</summary>
    private static Task<Window> PopupAsync(MainWindow owner, string title) => Wait.For(() =>
        AllWindows().FirstOrDefault(w => w != owner && w.IsVisible && w.Title?.StartsWith(title, StringComparison.Ordinal) == true),
        $"the pop-up \"{title}\" to show");

    private static readonly List<Window> Opened = [];

    static FilesPageUiTests() => Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
    {
        lock (Opened)
        {
            if (sender is Window w)
                Opened.Add(w);
        }
    });

    private static List<Window> AllWindows()
    {
        lock (Opened)
            return [.. Opened];
    }

    [AvaloniaFact]
    public async Task TheFilesTile_ShowsTheNestsFoldersAndFiles_AndAFolderOpensOnItsRow()
    {
        var (window, actions, nest) = Open();
        Person.Click(Screen.Named<RadioButton>(window, "NavFiles"));
        Assert.Equal(MainPage.Files, window.Page);
        var page = window.FilesView;
        Assert.Equal(["Documents", "Photos", "Videos", "notes.txt"], page.Rows.Select(r => r.Name));
        Assert.Equal(["Your nest"], page.CrumbTexts);
        Assert.Equal("Only on the nest", page.Rows.Single(r => r.Name == "Photos").WhereText);
        Assert.Equal(FilesPageText.Footer(nest.BytesOnNest(), nest.BytesHere(string.Empty)), page.FooterLine);

        await page.OpenAsync(page.Rows.Single(r => r.Name == "Documents"));
        Assert.Equal("Documents", page.Folder);
        Assert.Equal(["Your nest", "Documents"], page.CrumbTexts);
        Assert.Equal(["taxes", "report.docx"], page.Rows.Select(r => r.Name));

        await page.OpenAsync(page.Rows.Single(r => r.Name == "report.docx"));
        Assert.Contains("open-file:" + nest.OpenedPath, actions.Calls);

        // Searching looks through every folder.
        page.SearchText = "2025";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2025.pdf", Assert.Single(page.Rows).Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDeletedTab_IsTheHistoryPage()
    {
        var (window, _, _) = Open();
        window.Navigate(MainPage.Files);
        await window.LoadHistoryAsync();
        Person.Click(Screen.Named<RadioButton>(window, "DeletedFilesTab"));
        Assert.Equal(MainPage.History, window.Page);
        Assert.Equal(4, window.HistoryRows);
        Assert.Equal("4", Screen.Named<TextBlock>(window, "DeletedTabCount").Text);
        Assert.False(window.FilesView.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void WhileTheNestCantBeReached_TheBannerShowsAndNothingCanBeAdded()
    {
        var (window, _, nest) = Open();
        nest.NestOffline = true;
        window.Navigate(MainPage.Files);
        Assert.True(window.FilesView.OfflineShown);
        Assert.False(window.FilesView.AddEnabled);
        Assert.Equal(4, window.FilesView.Rows.Count); // the last list seen still shows
        window.Close();
    }

    [AvaloniaFact]
    public async Task KeepingAFolderOnlyOnTheNest_AsksFirst_AndSavesOnlyOnYes()
    {
        var (window, _, nest) = Open();
        window.Navigate(MainPage.Files);
        var choosing = window.FilesView.ChoosePlacementAsync("Documents", FilePlacement.NestOnly);
        var question = await PopupAsync(window, "Keep Documents only on your nest?");
        Person.Click(Screen.Find<Button>(question, "Cancel"));
        Assert.False(await choosing);
        Assert.Empty(nest.Calls);

        choosing = window.FilesView.ChoosePlacementAsync("Documents", FilePlacement.NestOnly);
        question = await PopupAsync(window, "Keep Documents only on your nest?");
        Person.Click(Screen.Find<Button>(question, "Free " + Format.Bytes(nest.BytesHere("Documents"))));
        Assert.True(await choosing);
        Assert.Equal(["place:Documents:NestOnly"], nest.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AChangeOnAFullNest_ShowsTheNestFullPopUp_AndHelpMeCleanUpOpensCleanUp()
    {
        var (window, _, nest) = Open();
        nest.NestFull = true;
        var file = Path.Combine(Path.GetTempPath(), "pairnets-full-test-mac.txt");
        await File.WriteAllTextAsync(file, "x");
        window.Navigate(MainPage.Files);
        _ = window.FilesView.AddAsync([file]);
        var full = await PopupAsync(window, FilesPageText.FullTitle);
        Person.Click(Screen.Find<Button>(full, FilesPageText.HelpMeCleanUp));
        await Wait.Until(() => window.CleanupShown, "Clean up to show");
        Assert.Equal("wedding-full-day.mp4", window.Cleanup.Rows[0].Name); // biggest first
        Assert.True(window.Cleanup.HistoryRowShown);
        Assert.Contains(FilesPageText.AllKinds, window.Cleanup.ChipTexts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheStrongQuestion_DeletesForGoodOnlyWithTheBoxTickedAndTheWordTyped()
    {
        var (window, _, nest) = Open();
        window.ShowCleanup();
        var biggest = window.Cleanup.Rows[0].Entry;
        var deleting = window.Cleanup.DeleteForGoodAsync(biggest);
        var sure = await PopupAsync(window, "Delete “wedding-full-day.mp4” for good?");
        var delete = Screen.Named<Button>(sure, "DeleteForGoodButton");
        Assert.False(delete.IsEnabled);
        Person.Click(Screen.Named<CheckBox>(sure, "SureCheck"));
        Assert.False(delete.IsEnabled); // ticked, not typed
        Screen.Named<TextBox>(sure, "SureWord").Text = "delete";
        Dispatcher.UIThread.RunJobs();
        Assert.False(delete.IsEnabled); // the word, but not in capitals
        Screen.Named<TextBox>(sure, "SureWord").Text = NestFiles.DeleteWord;
        Dispatcher.UIThread.RunJobs();
        Assert.True(delete.IsEnabled);
        Person.Click(Screen.Named<CheckBox>(sure, "SureCheck"));
        Assert.False(delete.IsEnabled); // unticked again
        Person.Click(Screen.Named<CheckBox>(sure, "SureCheck"));
        Person.Click(delete);
        await deleting;
        Assert.Contains($"purge:{biggest.Path}:DELETE", nest.Calls);
        Assert.False(nest.Has(biggest.Path));
        Assert.DoesNotContain(window.Cleanup.Rows, r => r.Entry.Path == biggest.Path);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFolderFreedAfterItsChoice_NoLongerSaysAllOnThisPc()
    {
        var nest = new FakeNestFiles();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = "/Users/me/Work", DeviceName = "MacBook", FirstRunCompleted = true };
        var view = new SettingsView(settings, new MemorySecrets(), autoStart: false, files: nest) { FilesSource = () => nest };
        var host = new Window { Content = view, Width = 900, Height = 780 };
        host.Show();
        Assert.EndsWith("all on this PC", view.PlacementSizeShown("Documents"));

        var choosing = view.ChoosePlacementAsync("Documents", FilePlacement.NestOnly);
        var question = await Wait.For(() => AllWindows().FirstOrDefault(w => w != host && w.IsVisible && w.Title == "Keep Documents only on your nest?"), "the question");
        Person.Click(Screen.Find<Button>(question, "Free " + Format.Bytes(nest.BytesHere("Documents"))));
        await choosing;

        // The sync frees the files a moment later; the next status the app shows reads the line again.
        var now = DateTimeOffset.Now;
        nest.Put("Documents/report.docx", 2_400_000, now, here: false);
        nest.Put("Documents/taxes/2025.pdf", 800_000, now, here: false);
        view.ShowPlacementStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" });
        Assert.Equal(FilesPageText.SizeLine(nest.List(string.Empty).Single(e => e.Path == "Documents").Size, 0), view.PlacementSizeShown("Documents"));
        host.Close();
    }

    [AvaloniaFact]
    public async Task TheSettingsCard_ListsTheTopFolders_AsksFirst_AndAppliesTheChoiceAtOnce()
    {
        var nest = new FakeNestFiles();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = "/Users/me/Work", DeviceName = "MacBook", FirstRunCompleted = true };
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var view = new SettingsView(settings, new MemorySecrets(), autoStart: false, files: nest);
        var applied = new List<ClientSettings>();
        view.SwitchesChanged += applied.Add;
        var host = new Window { Content = view, Width = 900, Height = 780 };
        host.Show();
        Assert.Equal(["", "Documents", "Photos", "Videos"], view.PlacementFolders);
        Assert.Equal(FilePlacement.NestOnly, view.PlacementChosen("Photos"));

        var choosing = view.ChoosePlacementAsync("Videos", FilePlacement.NestOnly);
        var question = await Wait.For(() => AllWindows().FirstOrDefault(w => w != host && w.IsVisible && w.Title == "Keep Videos only on your nest?"), "the question");
        Person.Click(Screen.Find<Button>(question, "Keep it on the nest only")); // nothing of it is here
        await choosing;
        Assert.Equal(FilePlacement.NestOnly, view.PlacementChosen("Videos"));
        Assert.Empty(nest.Calls); // through the app's settings, like the switches
        Assert.Equal(FilePlacement.NestOnly, applied.Single().Placement["Videos"]); // at once (A11)
        Assert.Equal(FilePlacement.NestOnly, view.PlacementToSave["Videos"]);

        // Without a nest the card shows, greyed out, with a note.
        var off = new SettingsView(settings, new MemorySecrets(), autoStart: false);
        host.Content = off;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FilesPageText.SettingsOff, Screen.Named<TextBlock>(off, "PlacementNote").Text);
        Assert.False(Screen.Named<StackPanel>(off, "PlacementRows").IsEnabled);
        host.Close();
    }

    /// <summary>The choices the card offers on <paramref name="folder"/>'s row, by their names.</summary>
    private static List<string> Offered(SettingsView view, string folder)
    {
        var row = folder.Length == 0 ? FilesPageText.EverythingElse : folder;
        return Screen.All<RadioButton>(Screen.Named<StackPanel>(view, "PlacementRows"))
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b) ?? string.Empty)
            .Where(n => n.StartsWith(row + ": ", StringComparison.Ordinal))
            .Select(n => n[(row.Length + 2)..])
            .ToList();
    }

    /// <summary>Every text a pop-up shows, joined.</summary>
    private static string Words(Window popup) => string.Join(" | ", Screen.All<TextBlock>(popup).Select(t => t.Text));

    [AvaloniaFact]
    public async Task TheWholeFolderRow_NeverOffersOnlyOnTheNest_AndNamesWhatAChoiceChanges()
    {
        var nest = new FakeNestFiles();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = "/Users/me/Work", DeviceName = "MacBook", FirstRunCompleted = true };
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var view = new SettingsView(settings, new MemorySecrets(), autoStart: false, files: nest);
        var applied = new List<ClientSettings>();
        view.SwitchesChanged += applied.Add;
        var host = new Window { Content = view, Width = 900, Height = 780 };
        host.Show();

        Assert.DoesNotContain("Only on the nest", Offered(view, ""));
        Assert.Contains("On this PC", Offered(view, ""));
        Assert.Contains("Only on the nest", Offered(view, "Videos"));

        // Asked for anyway: nothing is asked, saved or applied.
        await view.ChoosePlacementAsync("", FilePlacement.NestOnly);
        Assert.Empty(applied);
        Assert.Equal(FilePlacement.OnThisPc, view.PlacementChosen(""));
        Assert.Null(view.PlacementStatusShown);

        // On-Demand for the whole folder names the loose files and every folder without its own choice first.
        var choosing = view.ChoosePlacementAsync("", FilePlacement.OnDemand);
        var question = await Wait.For(() => AllWindows().FirstOrDefault(w => w != host && w.IsVisible && w.Title == "Make all your files On-Demand?"), "the question");
        Assert.Contains("This changes: 1 file right in your Pairnets folder (not in a folder), Documents (2 files), Videos (1 file).", Words(question));
        Person.Click(Screen.Find<Button>(question, "Make it On-Demand"));
        await choosing;
        Assert.Equal(FilePlacement.OnDemand, applied.Single().Placement[""]);
        Assert.Equal("Saved: All other files → On-Demand.", view.PlacementStatusShown);
        host.Close();
    }

    [AvaloniaFact]
    public async Task AChoice_StartsTheSyncAgain_WithoutACrash_AndTheCardFollowsTheDownload()
    {
        var first = new FakeNestFiles();
        INestFiles running = first;
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = "/Users/me/Work", DeviceName = "MacBook", FirstRunCompleted = true };
        settings.Placement["Videos"] = FilePlacement.NestOnly;
        var view = new SettingsView(settings, new MemorySecrets(), autoStart: false, files: first) { FilesSource = () => running };
        view.SwitchesChanged += _ =>
        {
            // As the app does: the old sync's list is closed and a new one runs.
            first.Closed = true;
            var next = new FakeNestFiles();
            next.Placement.Clear();
            running = next;
        };
        var host = new Window { Content = view, Width = 900, Height = 780 };
        host.Show();

        await view.ChoosePlacementAsync("Videos", FilePlacement.OnThisPc);
        Assert.Equal(FilePlacement.OnThisPc, view.PlacementChosen("Videos"));
        Assert.Equal("Saved: Videos → On this computer.", view.PlacementStatusShown);

        var downloading = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", CurrentPath = "Videos/wedding-full-day.mp4", Operation = "download",
            FilesTotal = 149, FilesDone = 37, Active = [new ActiveTransfer("Videos/wedding-full-day.mp4", "download", 10, 100)],
        };
        view.ShowPlacementStatus(downloading);
        Assert.StartsWith("Saved: Videos → On this computer. Downloading", view.PlacementStatusShown);
        Assert.Contains("37 of 149 files", view.PlacementStatusShown);

        // Even a page with no new list to read draws again without a crash.
        var stale = new SettingsView(settings, new MemorySecrets(), autoStart: false, files: first);
        host.Content = stale;
        await stale.ChoosePlacementAsync("Documents", FilePlacement.OnThisPc);
        host.Close();
    }

    [AvaloniaFact]
    public async Task TheFilesPage_ChoosesForExactlyTheFolderPicked_NeverTheWholeFolder()
    {
        var (window, _, nest) = Open();
        window.Navigate(MainPage.Files);
        Assert.False(await window.FilesView.ChoosePlacementAsync("", FilePlacement.NestOnly));
        Assert.Empty(nest.Calls);

        var choosing = window.FilesView.ChoosePlacementAsync("Videos", FilePlacement.NestOnly);
        var question = await PopupAsync(window, "Keep Videos only on your nest?");
        Assert.Contains("This changes: 1 file right in Videos.", Words(question));
        Person.Click(Screen.Find<Button>(question, "Keep it on the nest only"));
        Assert.True(await choosing);
        Assert.Equal(["place:Videos:NestOnly"], nest.Calls);
        Assert.DoesNotContain("", nest.Placement.Keys);
        window.Close();
    }
}
