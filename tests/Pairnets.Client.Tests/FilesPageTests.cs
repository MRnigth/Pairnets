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
    public async Task TheSettingsCard_ListsTheTopFolders_AsksFirst_AndSavesTheChoices()
    {
        var nest = new FakeNestFiles();
        var script = new DialogScript { Chooses = choice => choice.Buttons[^1] };
        using var answers = script.Install();
        var settings = new ClientSettings { ServerUrl = "https://nest.example.com/", Folder = @"D:\Sync", DeviceName = "DESKTOP", FirstRunCompleted = true };
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var view = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets(), files: nest));
        Assert.Equal(["", "Documents", "Photos", "Videos"], await Wpf.Ui(() => view.PlacementFolders.ToList()));
        Assert.Equal(FilePlacement.NestOnly, await Wpf.Ui(() => view.PlacementChosen("Photos")));

        await Wpf.Ui(() => view.ChoosePlacement("Videos", FilePlacement.NestOnly));
        Assert.Equal("Keep Videos only on your nest?", script.Popups.Items.Single());
        Assert.Equal(FilePlacement.NestOnly, await Wpf.Ui(() => view.PlacementChosen("Videos")));
        Assert.Empty(nest.Calls); // saved with Save, not at once

        // Without a nest the card shows, greyed out, with a note.
        var off = await Wpf.Ui(() => new SettingsView(settings, new FakeSecrets()));
        Assert.Equal(FilesPageText.SettingsOff, await Wpf.Ui(() => off.PlacementNote.Text));
        Assert.False(await Wpf.Ui(() => off.PlacementRows.IsEnabled));
        Assert.Empty(Wpf.TakeCrashes());
    }
}
