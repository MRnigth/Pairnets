using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>Where a row lives → its icon: a computer, a cloud with an arrow (On-Demand) or a cloud (only on the nest).</summary>
public sealed class WhereIconConverter : IValueConverter
{
    public static string Key(FilePlacement placement) => placement switch
    {
        FilePlacement.OnDemand => "I.CloudDown",
        FilePlacement.NestOnly => "I.Cloud",
        _ => "I.Desktop",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Visuals.Resource<Geometry>(Key(value is FilePlacement p ? p : FilePlacement.OnThisPc));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Where a row lives → its colour: green on this PC, amber On-Demand, blue only on the nest.</summary>
public sealed class WhereBrushConverter : IValueConverter
{
    public static string Key(FilePlacement placement) => placement switch
    {
        FilePlacement.OnDemand => "T.Warn",
        FilePlacement.NestOnly => "T.Accent",
        _ => "T.Ok",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Visuals.Resource<IBrush>(Key(value is FilePlacement p ? p : FilePlacement.OnThisPc));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A row of the Files page → its icon: a folder, or a file.</summary>
public sealed class RowIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Visuals.Resource<Geometry>(value is true ? "I.Folder" : "I.File");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// "All files" on the Files page: the folders and files on the nest (from this computer's copy of its list, so it
/// shows also while the nest can't be reached), where each folder lives on this computer, and opening, adding, saving a
/// copy, renaming, moving and deleting. A folder opens on a double-click; a file opens in its program.
/// </summary>
public partial class NestFilesView : UserControl
{
    private readonly ObservableCollection<NestRow> _rows = [];
    private string _folder = string.Empty;
    private INestFiles? _shown;
    private DateTimeOffset? _lastSync;

    public NestFilesView()
    {
        InitializeComponent();
        EntryList.ItemsSource = _rows;
        FilesSearch.TextChanged += (_, _) => Refresh();
        EntryList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null && EntryList.SelectedItem is NestRow row)
                _ = OpenAsync(row);
        };
        EntryList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && EntryList.SelectedItem is NestRow row)
                _ = OpenAsync(row);
        };
        ListCard.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && Files is { NestOffline: false } ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        ListCard.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 })
                _ = AddAsync(paths);
        });
        PickOnDemand.IsVisible = OnDemandOffered;
    }

    /// <summary>On-Demand is offered only where Windows' cloud files exist.</summary>
    public static bool OnDemandOffered => NestFiles.Choices.Contains(FilePlacement.OnDemand);

    /// <summary>The main window the page is in.</summary>
    public MainWindow? Host { get; set; }

    private INestFiles? Files => Host?.Actions?.Files;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The folder shown ("" for the top of the nest).</summary>
    public string Folder => _folder;

    /// <summary>The rows the list shows.</summary>
    public IReadOnlyList<NestRow> Rows => _rows;

    public string FooterLine => FooterText.Text ?? string.Empty;

    public string MessageText => FilesMessage.IsVisible ? FilesMessageText.Text ?? string.Empty : string.Empty;

    public string EmptyLine => EmptyMessage.IsVisible ? EmptyText.Text ?? string.Empty : string.Empty;

    public bool OfflineShown => OfflineBanner.IsVisible;

    /// <summary>The placement the strip above the list shows for this folder.</summary>
    public string PlacementShown => PlacementText.Text ?? string.Empty;

    public bool AddEnabled => AddFilesButton.IsEnabled;

    public string SearchText
    {
        get => FilesSearch.Text ?? string.Empty;
        set => FilesSearch.Text = value;
    }

    /// <summary>The path above the list, as its buttons read ("Your nest", "Photos", …).</summary>
    public IReadOnlyList<string> CrumbTexts => CrumbPanel.Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty).ToList();

    /// <summary>True while the "where this folder lives" menu is open.</summary>
    public bool PlacementMenuOpen => PlacementMenu.IsOpen;

    // ------------------------------------------------------------------ showing

    /// <summary>Shows <paramref name="folder"/>; with <paramref name="choosePlacement"/> its "where it lives" menu opens too.</summary>
    public void ShowFolder(string folder, bool choosePlacement = false)
    {
        _folder = PlacementMap.NormalizeFolder(folder);
        FilesMessage.IsVisible = false;
        if (SearchText.Length > 0)
            FilesSearch.Text = string.Empty; // reads the list again
        else
            Refresh();
        if (choosePlacement)
            Dispatcher.UIThread.Post(OpenPlacementMenu, DispatcherPriority.Loaded);
    }

    /// <summary>The status changed: the offline banner, and the list again when the nest's list changed.</summary>
    public void ShowStatus(StatusSnapshot status)
    {
        var files = Files;
        OfflineBanner.IsVisible = files is { NestOffline: true };
        OfflineRun.Text = FilesPageText.OfflineText;
        AddFilesButton.IsEnabled = files is { NestOffline: false };
        if (!IsEffectivelyVisible)
            return;
        if (!ReferenceEquals(files, _shown) || status.LastSyncAt != _lastSync)
        {
            _lastSync = status.LastSyncAt;
            Refresh();
        }
    }

    /// <summary>Reads the list again (the page opened, Refresh, or after a change).</summary>
    public void Refresh()
    {
        var files = _shown = Files;
        _rows.Clear();
        DrawCrumbs();
        OfflineBanner.IsVisible = files is { NestOffline: true };
        OfflineRun.Text = FilesPageText.OfflineText;
        AddFilesButton.IsEnabled = files is { NestOffline: false };
        PlacementButton.IsEnabled = files is not null;
        if (files is null)
        {
            ShowEmpty(FilesPageText.NotConnected);
            FooterText.Text = string.Empty;
            return;
        }
        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            foreach (var entry in files.Search(search))
                _rows.Add(new NestRow(entry, "in " + (PathRules.Parent(entry.Path) ?? FilesPageText.Top)));
        }
        else
        {
            foreach (var entry in files.List(_folder))
                _rows.Add(new NestRow(entry));
        }
        ShowEmpty(_rows.Count > 0 ? null : search.Length > 0 ? FilesPageText.NoMatch : FilesPageText.Empty);
        var placement = files.PlacementOf(_folder);
        PlacementText.Text = NestFiles.PlacementText(placement);
        PlacementIcon.Data = Visuals.Resource<Geometry>(WhereIconConverter.Key(placement));
        PlacementIcon.Stroke = Visuals.Resource<IBrush>(WhereBrushConverter.Key(placement));
        LivesLabel.Text = _folder.Length == 0 ? "Your files live" : "This folder lives";
        var total = search.Length > 0 ? _rows.Sum(r => r.Entry.Size) : files.List(_folder).Sum(e => e.Size);
        FooterText.Text = search.Length > 0
            ? $"{Format.Count(_rows.Count)} {(_rows.Count == 1 ? "file" : "files")} found · {Format.Bytes(total)}"
            : FilesPageText.Footer(total, files.BytesHere(_folder));
    }

    private void ShowEmpty(string? text)
    {
        EmptyMessage.IsVisible = text is not null;
        EmptyText.Text = text ?? string.Empty;
    }

    /// <summary>"Your nest › Photos › 2024": each step is a button that goes back to it.</summary>
    private void DrawCrumbs()
    {
        CrumbPanel.Children.Clear();
        var crumbs = FilesPageText.Crumbs(_folder);
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            if (i > 0)
            {
                CrumbPanel.Children.Add(new LineIcon
                {
                    Width = 13, Height = 13, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center,
                    Data = Visuals.Resource<Geometry>("I.ChevronRight"), Stroke = Visuals.Resource<IBrush>("T.TextSoft"),
                });
            }
            var button = new Button { Content = crumb.Name, FontSize = 15, Padding = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center };
            button.Classes.Add(i == crumbs.Count - 1 ? "link2" : "link");
            ToolTip.SetTip(button, crumb.Folder.Length == 0 ? "The top of your nest" : crumb.Folder);
            button.Click += (_, _) => ShowFolder(crumb.Folder);
            CrumbPanel.Children.Add(button);
        }
    }

    // ------------------------------------------------------------------ where this folder lives

    private void OnPlacementMenu(object? sender, RoutedEventArgs e) => OpenPlacementMenu();

    private void OpenPlacementMenu()
    {
        if (Files is not { } files)
            return;
        var now = files.PlacementOf(_folder);
        var here = files.BytesHere(_folder);
        foreach (var (item, placement) in new[] { (PickOnThisPc, FilePlacement.OnThisPc), (PickOnDemand, FilePlacement.OnDemand), (PickNestOnly, FilePlacement.NestOnly) })
        {
            var title = new TextBlock { Text = NestFiles.PlacementText(placement) + (placement == now ? "  ✓" : string.Empty), FontWeight = FontWeight.Bold };
            var line = new TextBlock { Text = FilesPageText.ChoiceText(placement, here), Classes = { "t3" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
            item.Header = new StackPanel { Margin = new Thickness(0, 4), Children = { title, line } };
            Avalonia.Automation.AutomationProperties.SetName(item, NestFiles.PlacementText(placement));
        }
        Avalonia.Automation.AutomationProperties.SetName(PlacementMenu, FilesPageText.PickerTitle(_folder));
        PlacementMenu.Open(PlacementButton);
    }

    private void OnPickPlacement(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && FromTag(tag) is { } placement)
            _ = ChoosePlacementAsync(_folder, placement);
    }

    private void OnRowPlace(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag, DataContext: NestRow row } && FromTag(tag) is { } placement)
            _ = ChoosePlacementAsync(row.Entry.Path, placement);
    }

    /// <summary>Asks first when files leave this computer, then saves the choice (syncing starts again with it).</summary>
    public async Task<bool> ChoosePlacementAsync(string folder, FilePlacement placement)
    {
        if (Files is not { } files)
            return false;
        var now = files.PlacementOf(folder);
        if (now == placement || !await Dialogs.ConfirmAwayAsync(Owner, folder, now, placement, files.BytesHere(folder)))
            return false;
        files.SetPlacement(folder, placement);
        Say($"{FilesPageText.FolderName(folder)}: {NestFiles.PlacementText(placement)}. Pairnets moves the files now.", ok: true);
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        return true;
    }

    /// <summary>A placement menu item's tag ("ThisPc", "Demand", "Nest") → its choice.</summary>
    private static FilePlacement? FromTag(string tag) => tag switch
    {
        "ThisPc" => FilePlacement.OnThisPc,
        "Demand" => FilePlacement.OnDemand,
        "Nest" => FilePlacement.NestOnly,
        _ => null,
    };

    // ------------------------------------------------------------------ a row's "…" menu

    private ContextMenu? _rowMenu;

    /// <summary>True while a row's "…" menu is open.</summary>
    public bool RowMenuOpen => _rowMenu?.IsOpen == true;

    private void OnRowMore(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            _rowMenu = menu;
            menu.Open(button);
        }
    }

    private static NestRow? RowOf(object? sender) => (sender as Control)?.DataContext as NestRow;

    private void OnRowOpen(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = OpenAsync(row);
    }

    private void OnRowSaveCopy(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = SaveCopyAsync(row.Entry);
    }

    private void OnRowRename(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = RenameAsync(row.Entry);
    }

    private void OnRowMove(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = MoveAsync(row.Entry);
    }

    private void OnRowDelete(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = DeleteAsync(row.Entry);
    }

    /// <summary>A folder opens in the list; a file opens in its program (from the nest when it isn't here).</summary>
    public async Task OpenAsync(NestRow row)
    {
        if (row.IsFolder)
        {
            ShowFolder(row.Entry.Path);
            return;
        }
        if (Files is not { } files)
            return;
        var result = await files.OpenAsync(row.Entry.Path);
        if (Handle(result) && result.OpenPath is { } path)
            Host?.Actions?.OpenFile(path);
    }

    /// <summary>Saves a copy of a file where the person chooses.</summary>
    public async Task SaveCopyAsync(NestEntry entry)
    {
        if (Files is not { } files || await Dialogs.SaveFileAsync(Owner, "Save a copy of " + entry.Name, entry.Name) is not { } destination)
            return;
        if (Handle(await files.SaveCopyAsync(entry.Path, destination)))
            Say($"Saved a copy of {entry.Name} to {destination}.", ok: true);
    }

    public async Task RenameAsync(NestEntry entry)
    {
        if (Files is not { } files
            || await Dialogs.PromptAsync(Owner, "Rename " + entry.Name, "The new name, on your nest and all your computers:", entry.Name, "Rename") is not { } name
            || name.Trim() == entry.Name)
            return;
        if (Handle(await files.RenameAsync(entry.Path, name)))
            Say($"Renamed to {name.Trim()}.", ok: true);
        Refresh();
    }

    public async Task MoveAsync(NestEntry entry)
    {
        if (Files is not { } files)
            return;
        var choices = new List<string> { FilesPageText.MoveTop };
        choices.AddRange(files.Folders(depth: 10).Where(f => f != entry.Path && !f.StartsWith(entry.Path + "/", StringComparison.Ordinal)
            && f != (PathRules.Parent(entry.Path) ?? string.Empty)));
        if (await Dialogs.PickOneAsync(Owner, $"Move {entry.Name} to…", "Choose the folder on your nest:", choices, "Move") is not { } pick)
            return;
        var to = pick == FilesPageText.MoveTop ? string.Empty : pick;
        if (Handle(await files.MoveAsync(entry.Path, to)))
            Say($"Moved {entry.Name} to {(to.Length == 0 ? FilesPageText.MoveTop.ToLowerInvariant() : to)}.", ok: true);
        Refresh();
    }

    public async Task DeleteAsync(NestEntry entry)
    {
        if (Files is not { } files || !await Dialogs.ConfirmAsync(Owner, "Pairnets – delete", FilesPageText.DeleteQuestion(entry), "Delete", "Cancel"))
            return;
        if (Handle(await files.DeleteAsync(entry.Path)))
            Say($"Deleted {entry.Name}. It is in the Deleted tab for 30 days.", ok: true);
        Refresh();
    }

    // ------------------------------------------------------------------ adding

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.OpenFilesAsync(Owner, "Add files to " + (_folder.Length == 0 ? FilesPageText.Top.ToLowerInvariant() : PathRules.FileName(_folder))) is { Count: > 0 } chosen)
            await AddAsync(chosen);
    }

    /// <summary>Adds files from this computer to the folder shown (folders dropped are left out).</summary>
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        if (Files is not { } files)
            return;
        var only = paths.Where(File.Exists).ToList();
        if (only.Count == 0)
        {
            Say("Only files can be added; to add a folder, add the files in it.", ok: false);
            return;
        }
        if (Handle(await files.AddAsync(only, _folder)))
            Say(only.Count == 1 ? $"Added {Path.GetFileName(only[0])}." : $"Added {only.Count} files.", ok: true);
        Refresh();
    }

    private void OnFilesRefresh(object? sender, RoutedEventArgs e)
    {
        FilesMessage.IsVisible = false;
        Refresh();
    }

    private void OnCleanUp(object? sender, RoutedEventArgs e) => Host?.ShowCleanup();

    // ------------------------------------------------------------------ results

    /// <summary>True when it went well; a full nest shows its pop-up, any other problem its words.</summary>
    private bool Handle(NestResult result)
    {
        if (result.Ok)
            return true;
        if (result.NestFull)
            _ = Host?.ShowNestFullAsync();
        else
            Say(result.Problem ?? "That did not work.", ok: false);
        return false;
    }

    private void Say(string message, bool ok)
    {
        FilesMessage.IsVisible = true;
        FilesMessage.Classes.Set("ok", ok);
        FilesMessage.Classes.Set("bad", !ok);
        FilesMessageText.Text = message;
    }
}
