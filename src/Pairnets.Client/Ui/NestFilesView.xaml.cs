using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Ui;

/// <summary>Where a row lives → its icon: a computer, a cloud with an arrow (On-Demand) or a cloud (only on the nest).</summary>
public sealed class WhereIconConverter : IValueConverter
{
    public static string Key(FilePlacement placement) => placement switch
    {
        FilePlacement.OnDemand => "I.CloudDown",
        FilePlacement.NestOnly => "I.Cloud",
        _ => "I.Desktop",
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Visuals.Resource<Geometry>(Key(value is FilePlacement p ? p : FilePlacement.OnThisPc));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
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

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Visuals.Resource<Brush>(Key(value is FilePlacement p ? p : FilePlacement.OnThisPc));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// "All files" on the Files page: the folders and files on the nest (from this computer's copy of its list, so it
/// shows also while the nest can't be reached), where each folder lives on this PC, and opening, adding, saving a copy,
/// renaming, moving and deleting. A folder opens on a double-click; a file opens in its program.
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
        FilesSearch.TextChanged += Refresh;
        EntryList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && !InButton(e.OriginalSource as DependencyObject) && EntryList.SelectedItem is NestRow row)
                _ = OpenAsync(row);
        };
        EntryList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && EntryList.SelectedItem is NestRow row)
                _ = OpenAsync(row);
        };
        ListCard.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Files is { NestOffline: false } ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        ListCard.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                _ = AddAsync(dropped);
        };
        PickOnDemand.Visibility = NestFiles.Choices.Contains(FilePlacement.OnDemand) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }

    private INestFiles? Files => Host?.Actions?.Files;

    private Window? Owner => Window.GetWindow(this);

    /// <summary>The folder shown ("" for the top of the nest).</summary>
    public string Folder => _folder;

    /// <summary>The rows the list shows.</summary>
    public IReadOnlyList<NestRow> Rows => _rows;

    public string FooterLine => FooterText.Text;

    public string MessageText => FilesMessage.Visibility == Visibility.Visible ? FilesMessageText.Text : string.Empty;

    public string EmptyLine => EmptyMessage.Visibility == Visibility.Visible ? EmptyText.Text : string.Empty;

    public bool OfflineShown => OfflineBanner.Visibility == Visibility.Visible;

    /// <summary>True while the "where this folder lives" menu is open.</summary>
    public bool PlacementMenuOpen => PlacementMenu.IsOpen;

    /// <summary>The placement the strip above the list shows for this folder.</summary>
    public string PlacementShown => PlacementText.Text;

    public string SearchText
    {
        get => FilesSearch.Text;
        set => FilesSearch.Text = value;
    }

    /// <summary>The path above the list, as its buttons read ("Your nest", "Photos", …).</summary>
    public IReadOnlyList<string> CrumbTexts => CrumbPanel.Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty).ToList();

    // ------------------------------------------------------------------ showing

    /// <summary>Shows <paramref name="folder"/>; with <paramref name="choosePlacement"/> its "where it lives" menu opens too.</summary>
    public void ShowFolder(string folder, bool choosePlacement = false)
    {
        _folder = PlacementMap.NormalizeFolder(folder);
        FilesMessage.Visibility = Visibility.Collapsed;
        if (FilesSearch.Text.Length > 0)
            FilesSearch.Text = string.Empty; // reads the list again
        else
            Refresh();
        if (choosePlacement)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, OpenPlacementMenu);
    }

    /// <summary>The status changed: the offline banner, and the list again when the nest's list changed.</summary>
    public void ShowStatus(StatusSnapshot status)
    {
        var files = Files;
        OfflineBanner.Visibility = files is { NestOffline: true } ? Visibility.Visible : Visibility.Collapsed;
        OfflineRun.Text = FilesPageText.OfflineText;
        AddFilesButton.IsEnabled = files is { NestOffline: false };
        if (!IsVisible)
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
        OfflineBanner.Visibility = files is { NestOffline: true } ? Visibility.Visible : Visibility.Collapsed;
        OfflineRun.Text = FilesPageText.OfflineText;
        AddFilesButton.IsEnabled = files is { NestOffline: false };
        PlacementButton.IsEnabled = files is not null;
        if (files is null)
        {
            ShowEmpty(FilesPageText.NotConnected);
            FooterText.Text = string.Empty;
            return;
        }
        var search = FilesSearch.Text.Trim();
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
        PlacementIcon.SetResourceReference(LineIcon.StrokeProperty, WhereBrushConverter.Key(placement));
        LivesLabel.Text = _folder.Length == 0 ? "Your files live" : "This folder lives";
        var total = search.Length > 0 ? _rows.Sum(r => r.Entry.Size) : files.List(_folder).Sum(e => e.Size);
        FooterText.Text = search.Length > 0
            ? $"{Format.Count(_rows.Count)} {(_rows.Count == 1 ? "file" : "files")} found · {Format.Bytes(total)}"
            : FilesPageText.Footer(total, files.BytesHere(_folder));
    }

    /// <summary>A double-click on a row's "…" button is not a double-click on the row.</summary>
    private static bool InButton(DependencyObject? element)
    {
        for (var e = element; e is not null; e = e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : null)
        {
            if (e is Button)
                return true;
        }
        return false;
    }

    private void ShowEmpty(string? text)
    {
        EmptyMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
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
            var last = i == crumbs.Count - 1;
            if (i > 0)
                CrumbPanel.Children.Add(Parts.Icon("I.ChevronRight", 13, "T.TextSoft", 2.2));
            var button = new Button
            {
                Content = crumb.Name,
                Style = (Style)FindResource("PgLink"),
                FontSize = 15,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = crumb.Folder.Length == 0 ? "The top of your nest" : crumb.Folder,
            };
            if (last)
                button.SetResourceReference(ForegroundProperty, "T.Text");
            button.Click += (_, _) => ShowFolder(crumb.Folder);
            CrumbPanel.Children.Add(button);
        }
        foreach (var icon in CrumbPanel.Children.OfType<LineIcon>())
            icon.VerticalAlignment = VerticalAlignment.Center;
    }

    // ------------------------------------------------------------------ where this folder lives

    private void OnPlacementMenu(object sender, RoutedEventArgs e) => OpenPlacementMenu();

    private void OpenPlacementMenu()
    {
        if (Files is not { } files)
            return;
        var now = files.PlacementOf(_folder);
        var here = files.BytesHere(_folder);
        foreach (var (item, placement) in new[] { (PickOnThisPc, FilePlacement.OnThisPc), (PickOnDemand, FilePlacement.OnDemand), (PickNestOnly, FilePlacement.NestOnly) })
        {
            var title = Parts.Text(NestFiles.PlacementText(placement) + (placement == now ? "  ✓" : string.Empty), 14, "T.Text", FontWeights.Bold);
            var line = Parts.Text(FilesPageText.ChoiceText(placement, here), 12, "T.Muted", trim: false);
            line.TextWrapping = TextWrapping.Wrap;
            line.MaxWidth = 320;
            item.Header = new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { title, line } };
            item.IsChecked = placement == now;
        }
        System.Windows.Automation.AutomationProperties.SetName(PlacementMenu, FilesPageText.PickerTitle(_folder));
        PlacementMenu.PlacementTarget = PlacementButton;
        PlacementMenu.IsOpen = true;
    }

    private void OnPickPlacement(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && FromTag(tag) is { } placement)
            ChoosePlacement(_folder, placement);
    }

    private void OnRowPlace(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag, DataContext: NestRow row } && FromTag(tag) is { } placement)
            ChoosePlacement(row.Entry.Path, placement);
    }

    /// <summary>Asks first when files leave this PC, then saves the choice (syncing starts again with it).</summary>
    public bool ChoosePlacement(string folder, FilePlacement placement)
    {
        if (Files is not { } files)
            return false;
        var now = files.PlacementOf(folder);
        if (now == placement || !Dialogs.ConfirmAway(Owner, folder, now, placement, files.BytesHere(folder)))
            return false;
        files.SetPlacement(folder, placement);
        Say($"{FilesPageText.FolderName(folder)}: {NestFiles.PlacementText(placement)}. Pairnets moves the files now.", ok: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
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

    private void OnRowMore(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private static NestRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as NestRow;

    private void OnRowOpen(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = OpenAsync(row);
    }

    private void OnRowSaveCopy(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = SaveCopyAsync(row.Entry);
    }

    private void OnRowRename(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = RenameAsync(row.Entry);
    }

    private void OnRowMove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = MoveAsync(row.Entry);
    }

    private void OnRowDelete(object sender, RoutedEventArgs e)
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
        if (Files is not { } files || Dialogs.SaveFile(Owner, "Save a copy of " + entry.Name, entry.Name) is not { } destination)
            return;
        if (Handle(await files.SaveCopyAsync(entry.Path, destination)))
            Say($"Saved a copy of {entry.Name} to {destination}.", ok: true);
    }

    public async Task RenameAsync(NestEntry entry)
    {
        if (Files is not { } files || Dialogs.Prompt(Owner, "Rename " + entry.Name, "The new name, on your nest and all your computers:", entry.Name, "Rename") is not { } name
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
        if (Dialogs.PickOne(Owner, $"Move {entry.Name} to…", "Choose the folder on your nest:", choices, "Move") is not { } pick)
            return;
        var to = pick == FilesPageText.MoveTop ? string.Empty : pick;
        if (Handle(await files.MoveAsync(entry.Path, to)))
            Say($"Moved {entry.Name} to {(to.Length == 0 ? FilesPageText.MoveTop.ToLowerInvariant() : to)}.", ok: true);
        Refresh();
    }

    public async Task DeleteAsync(NestEntry entry)
    {
        if (Files is not { } files
            || Dialogs.Ask(Owner, FilesPageText.DeleteQuestion(entry), "Pairnets – delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (Handle(await files.DeleteAsync(entry.Path)))
            Say($"Deleted {entry.Name}. It is in the Deleted tab for 30 days.", ok: true);
        Refresh();
    }

    // ------------------------------------------------------------------ adding

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (Dialogs.OpenFiles(Owner, "Add files to " + (_folder.Length == 0 ? FilesPageText.Top.ToLowerInvariant() : PathRules.FileName(_folder))) is { Count: > 0 } chosen)
            _ = AddAsync(chosen);
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

    private void OnFilesRefresh(object sender, RoutedEventArgs e)
    {
        FilesMessage.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void OnCleanUp(object sender, RoutedEventArgs e) => Host?.ShowCleanup();

    // ------------------------------------------------------------------ results

    /// <summary>True when it went well; a full nest shows its pop-up, any other problem its words.</summary>
    private bool Handle(NestResult result)
    {
        if (result.Ok)
            return true;
        if (result.NestFull)
            Host?.ShowNestFull();
        else
            Say(result.Problem ?? "That did not work.", ok: false);
        return false;
    }

    private void Say(string message, bool ok)
    {
        FilesMessage.Visibility = Visibility.Visible;
        FilesMessage.SetResourceReference(Border.BackgroundProperty, ok ? "T.OkPill" : "T.BadPill");
        FilesMessageText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "T.OkText" : "T.BadText");
        FilesMessageText.Text = message;
    }
}
