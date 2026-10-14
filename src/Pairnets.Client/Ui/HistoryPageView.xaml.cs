using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>A file's path → the icon for its kind (picture, video, sheet, document, plain file).</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Visuals.Resource<Geometry>(Parts.FileIcon(value as string));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The History page. "Deleted files" is one table with a Restore button on every row (it brings back the newest stored
/// copy); "All files" is a list on the left and the versions of the chosen file on the right.
/// </summary>
public partial class HistoryPageView : UserControl
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1);

    private readonly ObservableCollection<ServerFile> _deleted = [];
    private readonly ObservableCollection<ServerFile> _shownFiles = [];
    private readonly TextBlock _versionsMessage;
    private IReadOnlyList<ServerFile>? _serverFiles;
    private DateTimeOffset _serverFilesAt;
    private CancellationTokenSource? _versionsCts;
    private string? _selectAfterLoad;
    private bool _navigating;
    private bool _deletedMode = true;
    private int _versionRows;

    public HistoryPageView()
    {
        InitializeComponent();
        DeletedList.ItemsSource = _deleted;
        FileList.ItemsSource = _shownFiles;
        HistorySearch.TextChanged += () =>
        {
            if (!_navigating)
                Filter();
        };
        _versionsMessage = Parts.Text(string.Empty, 14, "T.Muted", trim: false);
        _versionsMessage.TextWrapping = TextWrapping.Wrap;
        _versionsMessage.Margin = new Thickness(0, 10, 0, 0);
        UpdateTabs();
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }

    private IHistorySource? Source => Host?.Actions?.History;

    /// <summary>The rows of the All files list.</summary>
    public IReadOnlyList<ServerFile> FileRows => _shownFiles;

    /// <summary>The rows of the Deleted files table.</summary>
    public IReadOnlyList<ServerFile> DeletedRows => _deleted;

    /// <summary>How many stored versions the chosen file shows (without the "current version" row).</summary>
    public int VersionRowCount => _versionRows;

    public string VersionsMessageText => _versionsMessage.Text;

    public string RestoreText => RestoreMessageText.Text;

    public bool IsDeletedMode => _deletedMode;

    public string SearchText
    {
        get => HistorySearch.Text;
        set => HistorySearch.Text = value;
    }

    /// <summary>True when the table or list has something to show (not a message).</summary>
    public bool ShowsRows => HistoryMessage.Visibility != Visibility.Visible;

    public string MessageText => HistoryMessage.Visibility == Visibility.Visible ? HistoryMessageText.Text : string.Empty;

    /// <summary>The page was opened: read the files again when they are older than a minute.</summary>
    public void Opened()
    {
        if (_serverFiles is null || DateTimeOffset.Now - _serverFilesAt > FreshFor)
            _ = LoadAsync();
    }

    /// <summary>Forget what was read (the next open reads again).</summary>
    public void Forget() => _serverFiles = null;

    // ------------------------------------------------------------------ reading

    /// <summary>Reads the nest's file list (deleted files included).</summary>
    public async Task LoadAsync()
    {
        var source = Source;
        if (source is null)
        {
            ShowMessage("Not connected to your nest. History appears here once Pairnets is connected.", retry: false);
            return;
        }
        ShowMessage("Loading the files on your nest…", retry: false);
        try
        {
            _serverFiles = await source.GetServerFilesAsync(CancellationToken.None);
            _serverFilesAt = DateTimeOffset.Now;
            if (_selectAfterLoad is { } path && _serverFiles.FirstOrDefault(f => f.Path == path) is { } target)
            {
                _navigating = true;
                try
                {
                    _deletedMode = target.Deleted;
                    DeletedTab.IsChecked = target.Deleted;
                    FilesTab.IsChecked = !target.Deleted;
                    UpdateTabs();
                    HistorySearch.Text = target.Deleted ? target.Name : string.Empty;
                }
                finally
                {
                    _navigating = false;
                }
            }
            Filter();
            if (_selectAfterLoad is { } selected && !_deletedMode)
                FileList.SelectedItem = _shownFiles.FirstOrDefault(f => f.Path == selected);
            _selectAfterLoad = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowMessage("Could not read the files on your nest: " + ex.Message, retry: true);
        }
    }

    /// <summary>The next read chooses this file (from the activity list): its versions, or the deleted table filtered to it.</summary>
    public void ChooseAfterLoad(string path) => _selectAfterLoad = path;

    private void ShowMessage(string text, bool retry)
    {
        _deleted.Clear();
        _shownFiles.Clear();
        HistoryMessage.Visibility = Visibility.Visible;
        DeletedPane.Visibility = Visibility.Collapsed;
        FilesPane.Visibility = Visibility.Collapsed;
        HistoryMessageText.Text = text;
        HistoryRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        HistorySummary.Text = string.Empty;
    }

    private void UpdateTabs()
    {
        HistorySearch.Placeholder = _deletedMode ? "Search deleted files by name or folder" : "Search all files by name or folder";
    }

    private void Filter()
    {
        if (_serverFiles is null)
            return;
        var now = DateTimeOffset.UtcNow;
        var search = HistorySearch.Text;
        var (rows, total) = HistoryQuery.Filter(_serverFiles, _deletedMode, search, now);
        var deletedTotal = HistoryQuery.Filter(_serverFiles, deleted: true, null, now).Total;
        DeletedCount.Text = Format.Count(deletedTotal);
        var summary = HistoryQuery.Summary(rows.Count, total, _deletedMode, !string.IsNullOrWhiteSpace(search));
        var selected = (FileList.SelectedItem as ServerFile)?.Path;

        _deleted.Clear();
        _shownFiles.Clear();
        foreach (var row in rows)
            (_deletedMode ? _deleted : _shownFiles).Add(row);
        HistorySummary.Text = summary;

        var empty = rows.Count == 0;
        HistoryMessage.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryRetry.Visibility = Visibility.Collapsed;
        HistoryMessageIcon.Data = Visuals.Resource<Geometry>(_deletedMode ? "I.Trash" : "I.File");
        HistoryMessageText.Text = summary;
        DeletedPane.Visibility = !empty && _deletedMode ? Visibility.Visible : Visibility.Collapsed;
        FilesPane.Visibility = !empty && !_deletedMode ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null)
            FileList.SelectedItem = _shownFiles.FirstOrDefault(f => f.Path == selected);
        if (FileList.SelectedItem is null)
            ShowDetail(null);
    }

    private void OnHistoryModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || _navigating || FileList is null)
            return; // Checked also fires while the page is read from its XAML
        _deletedMode = tab == DeletedTab;
        UpdateTabs();
        Filter();
    }

    private void OnHistoryReload(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>Selects a row of the All files list (the screenshot tool).</summary>
    public void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    /// <summary>Switches to the All files tab.</summary>
    public void ShowAllFiles() => FilesTab.IsChecked = true;

    // ------------------------------------------------------------------ the chosen file and its versions

    private void OnFileSelected(object sender, SelectionChangedEventArgs e) => ShowDetail(FileList.SelectedItem as ServerFile);

    private async void ShowDetail(ServerFile? file)
    {
        _versionsCts?.Cancel();
        DetailEmpty.Visibility = file is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = file is null ? Visibility.Collapsed : Visibility.Visible;
        VersionList.Children.Clear();
        _versionRows = 0;
        RestoreMessage.Visibility = Visibility.Collapsed;
        VersionCount.Text = string.Empty;
        if (file is null)
            return;
        DetailName.Text = file.Name;
        DetailFolder.Text = file.InFolderText;
        DetailIcon.Data = Visuals.Resource<Geometry>(Parts.FileIcon(file.Path));
        ShowInFolderButton.Visibility = file.Deleted ? Visibility.Collapsed : Visibility.Visible;
        VersionList.Children.Add(CurrentRow(file));
        _versionsMessage.Text = "Loading versions…";
        VersionList.Children.Add(_versionsMessage);
        if (Source is not { } source)
            return;
        var cts = _versionsCts = new CancellationTokenSource();
        try
        {
            var versions = await source.GetVersionsAsync(file.Path, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            var rows = VersionRow.For(file, versions, DateTimeOffset.UtcNow);
            VersionList.Children.Remove(_versionsMessage);
            foreach (var row in rows)
                VersionList.Children.Add(VersionLine(row, last: row == rows[^1]));
            _versionRows = rows.Count;
            VersionCount.Text = rows.Count == 1 ? "1 older version" : $"{rows.Count} older versions";
            if (rows.Count == 0)
            {
                _versionsMessage.Text = file.Deleted
                    ? "Your nest no longer has a copy: it was deleted more than 30 days ago."
                    : "No older versions: this file has not been changed or replaced in the last 30 days.";
                VersionList.Children.Add(_versionsMessage);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                _versionsMessage.Text = "Could not read the versions: " + ex.Message;
        }
    }

    private static Border Timeline(UIElement dot, bool first, bool last)
    {
        var spine = new Grid { Width = 16 };
        var line = new Border { Width = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Center };
        line.SetResourceReference(Border.BackgroundProperty, "T.Track");
        line.VerticalAlignment = VerticalAlignment.Stretch;
        line.Margin = new Thickness(0, first ? 26 : 0, 0, last ? 26 : 0);
        spine.Children.Add(line);
        spine.Children.Add(dot);
        return new Border { Child = spine, Width = 16 };
    }

    private static FrameworkElement Dot(bool current)
    {
        var dot = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (current)
        {
            dot.Width = dot.Height = 16;
            dot.StrokeThickness = 3;
            dot.SetResourceReference(Shape.FillProperty, "T.Accent");
            dot.SetResourceReference(Shape.StrokeProperty, "T.Card");
        }
        else
        {
            dot.Width = dot.Height = 14;
            dot.StrokeThickness = 2;
            dot.SetResourceReference(Shape.FillProperty, "T.Card");
            dot.SetResourceReference(Shape.StrokeProperty, "T.TextSoft");
        }
        return dot;
    }

    private UIElement CurrentRow(ServerFile file)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 58 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var spine = Timeline(Dot(current: true), first: true, last: false);
        spine.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(spine);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Parts.Text(file.Deleted ? "Deleted " + file.WhenText : Capital(file.WhenText), 14, "T.Text", FontWeights.Bold));
        text.Children.Add(Parts.Text(file.Deleted ? "Newest stored copy first" : Format.Bytes(file.Size), 12, "T.TextSoft"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!file.Deleted)
        {
            var pill = new Border { Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, "T.OkPill");
            var inner = Parts.IconText("I.Check", "Current version", 13, "T.OkText", 2.8, 6);
            inner.Children.OfType<TextBlock>().First().FontWeight = FontWeights.Bold;
            inner.Children.OfType<TextBlock>().First().FontSize = 12;
            inner.Children.OfType<TextBlock>().First().SetResourceReference(TextBlock.ForegroundProperty, "T.OkText");
            pill.Child = inner;
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
        }
        return grid;
    }

    private UIElement VersionLine(VersionRow row, bool last)
    {
        var grid = new Grid { MinHeight = 58 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var spine = Timeline(Dot(current: false), first: false, last: last);
        spine.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(spine);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Parts.Text(row.Title, 14, "T.Text", FontWeights.Bold));
        text.Children.Add(Parts.Text(row.Detail, 12, "T.TextSoft"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var restore = new Button { Style = (Style)FindResource("PgSmallButton"), CommandParameter = row, VerticalAlignment = VerticalAlignment.Center, Content = Parts.IconText("I.Restore2", "Restore", 15, "T.Text", 2.2, 6) };
        restore.Click += OnRestore;
        Grid.SetColumn(restore, 2);
        grid.Children.Add(restore);
        return grid;
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is ServerFile file)
            Host?.Actions?.RevealFile(file.Path);
    }

    // ------------------------------------------------------------------ restoring

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: VersionRow row } button)
            return;
        button.IsEnabled = false;
        try
        {
            await RestoreAsync(row);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Restores one version, says how it went, and reads the list again (the file is current again).</summary>
    public async Task RestoreAsync(VersionRow row)
    {
        if (Source is not { } source)
            return;
        var error = await source.RestoreVersionAsync(row.Path, row.Version, CancellationToken.None);
        if (error is null)
        {
            _selectAfterLoad = row.Path;
            _serverFiles = null;
            await LoadAsync();
        }
        Say(error ?? $"Restored. {Pairnets.Core.Paths.PathRules.FileName(row.Path)} is back on your computers in a few seconds.", error is null);
    }

    private async void OnRestoreFile(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: ServerFile file } button)
            return;
        button.IsEnabled = false;
        try
        {
            await RestoreNewestAsync(file);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Brings back a deleted file: its newest stored copy goes back to the folder it came from.</summary>
    public async Task RestoreNewestAsync(ServerFile file)
    {
        if (Source is not { } source)
            return;
        string? error;
        try
        {
            var versions = await source.GetVersionsAsync(file.Path, CancellationToken.None);
            error = versions.Count == 0
                ? "Your nest no longer has a copy of " + file.Name + ": it was deleted more than 30 days ago."
                : await source.RestoreVersionAsync(file.Path, versions[0], CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = "Could not restore " + file.Name + ": " + ex.Message;
        }
        if (error is null)
        {
            _serverFiles = null;
            await LoadAsync();
        }
        Say(error ?? $"Restored. {file.Name} is back on your computers in a few seconds.", error is null);
    }

    private void Say(string message, bool ok)
    {
        RestoreMessage.Visibility = Visibility.Visible;
        RestoreMessage.SetResourceReference(Border.BackgroundProperty, ok ? "T.OkPill" : "T.BadPill");
        RestoreMessageText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "T.OkText" : "T.BadText");
        RestoreMessageText.Text = message;
    }
}
