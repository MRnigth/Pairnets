using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>An item in the "Needs attention" list with one action button.</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action);

/// <summary>The pages in the main window's sidebar.</summary>
public enum MainPage
{
    Overview,
    Activity,
    History,
    Devices,
    Attention,
    Settings,
}

/// <summary>What the main window's buttons do (implemented by <see cref="DesktopController"/>).</summary>
public interface IMainActions
{
    void FixBlocked();
    void SyncNow();
    void TogglePause();
    void OpenFolder();

    /// <summary>Opens the Settings page (the controller hands the form to <see cref="MainWindow.ShowSettingsPage"/>).</summary>
    void ShowSettings();
    void ViewLog();
    void UpdateNow();
    void DismissUpdate();
    void DownloadNow();
    void UpdateServer();
    void ReportBug();

    /// <summary>The Devices page was opened: ask the server for the current list.</summary>
    void RefreshDevices();

    /// <summary>"+ Add a computer…": how to add one (it signs in on the new computer).</summary>
    void AddComputer();

    /// <summary>Opens the nest's Devices page (rename, remove, approve).</summary>
    void ManageDevices();

    /// <summary>Shows a synced file in the file manager (or the folder, when the file is gone).</summary>
    void RevealFile(string syncPath);

    /// <summary>Where the History page reads from; null while not connected.</summary>
    IHistorySource? History { get; }
}

public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<ActivityItem> _recent = [];
    private readonly ObservableCollection<object> _activityRows = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<ActiveFileView> _active = [];
    private readonly ObservableCollection<ServerFile> _shownFiles = [];
    private readonly ObservableCollection<VersionRow> _versions = [];
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private IReadOnlyList<DeviceInfo>? _shownDevices;
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private IReadOnlyList<ServerFile>? _serverFiles;
    private DateTimeOffset _serverFilesAt;
    private CancellationTokenSource? _versionsCts;
    private string? _selectAfterLoad;
    private string? _shownState;
    private string? _tintKey;
    private bool _navigating;
    private bool _deletedMode = true;

    private static readonly TimeSpan HistoryFreshFor = TimeSpan.FromMinutes(1);

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(IMainActions? actions)
    {
        InitializeComponent();
        _actions = actions;
        RecentList.ItemsSource = _recent;
        ActivityList.ItemsSource = _activityRows;
        AttentionList.ItemsSource = _attention;
        ActiveList.ItemsSource = _active;
        FileList.ItemsSource = _shownFiles;
        VersionList.ItemsSource = _versions;
        DevicesList.ItemsSource = _devices;
        AppVersion.Text = "Pairnets " + PairnetsInfo.ProductVersion;
        UpdatePanels();
    }

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainPage Page { get; private set; } = MainPage.Overview;

    /// <summary>This computer's name, marked in the Devices list and shown first in the overview's picture.</summary>
    public string? ThisDevice { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && !e.IsProgrammatic)
        {
            e.Cancel = true; // keep running in the menu bar / tray
            Hide();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ navigation

    public void Navigate(MainPage page)
    {
        _navigating = true;
        try
        {
            NavFor(page).IsChecked = true;
        }
        finally
        {
            _navigating = false;
        }
        ShowPage(page);
    }

    private RadioButton NavFor(MainPage page) => page switch
    {
        MainPage.Activity => NavActivity,
        MainPage.History => NavHistory,
        MainPage.Devices => NavDevices,
        MainPage.Attention => NavAttention,
        MainPage.Settings => NavSettings,
        _ => NavOverview,
    };

    private void OnNav(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } nav || _navigating)
            return;
        var page = nav == NavActivity ? MainPage.Activity
            : nav == NavHistory ? MainPage.History
            : nav == NavDevices ? MainPage.Devices
            : nav == NavAttention ? MainPage.Attention
            : nav == NavSettings ? MainPage.Settings
            : MainPage.Overview;
        if (page == MainPage.Settings && _actions is not null)
        {
            _actions.ShowSettings(); // comes back through ShowSettingsPage with a fresh form
            return;
        }
        ShowPage(page);
    }

    private void ShowPage(MainPage page)
    {
        if (Page == MainPage.Settings && page != MainPage.Settings)
            SettingsPage.Content = null; // next time the form starts again from the saved settings
        Page = page;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            MainPage.Activity => ("Activity", "What synced recently, newest first. Right-click a file for more."),
            MainPage.History => ("History", "Get back deleted files and older versions. The server keeps them for 30 days."),
            MainPage.Devices => ("Devices", "Every computer that uses your server: online now, or when it was last seen."),
            MainPage.Attention => ("Needs attention", "Things Pairnets can't decide for you."),
            MainPage.Settings => ("Settings", "Your server, the folder, and how Pairnets behaves."),
            _ => ("Overview", string.Empty),
        };
        PageSubtitle.IsVisible = PageSubtitle.Text.Length > 0;
        OverviewPage.IsVisible = page == MainPage.Overview;
        ActivityPage.IsVisible = page == MainPage.Activity;
        HistoryPage.IsVisible = page == MainPage.History;
        DevicesPage.IsVisible = page == MainPage.Devices;
        AttentionPage.IsVisible = page == MainPage.Attention;
        SettingsPage.IsVisible = page == MainPage.Settings;
        foreach (var shown in new Control[] { OverviewPage, ActivityPage, HistoryPage, DevicesPage, AttentionPage, SettingsPage })
        {
            shown.Classes.Remove("enter");
            if (shown.IsVisible)
                shown.Classes.Add("enter");
        }
        if (page == MainPage.Activity)
            LiveLists.Sync(_activityRows, ActivityDays.Rows(_activityItems, DateTimeOffset.Now));
        if (page == MainPage.Devices)
            _actions?.RefreshDevices();
        if (page == MainPage.History && (_serverFiles is null || DateTimeOffset.Now - _serverFilesAt > HistoryFreshFor))
            _ = LoadHistoryAsync();
        UpdatePanels();
    }

    /// <summary>Shows the settings form as a page (the controller builds it from the current settings).</summary>
    public void ShowSettingsPage(Control form)
    {
        SettingsPage.Content = form;
        Navigate(MainPage.Settings);
    }

    /// <summary>Switches to the "Needs attention" page, or back to the overview (notifications, screenshots).</summary>
    public void ShowAttentionTab(bool attention) => Navigate(attention ? MainPage.Attention : MainPage.Overview);

    // ------------------------------------------------------------------ status

    public void ShowStatus(StatusSnapshot s, string? folder, string? device = null)
    {
        if (device is not null)
            ThisDevice = device;
        device ??= ThisDevice;
        ShowDevices(s);
        var (brush, icon) = s.IsWaiting ? ("S.Grey", "I.Wait") : Visuals.ForStatus(s.Status);
        StatusBadge.Fill = Visuals.Resource<IBrush>(brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
        Tint(brush);
        Animate(s);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText;
        Detail.IsVisible = s.DetailText.Length > 0;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        // Facts
        FolderText.Text = folder ?? "Not chosen yet";
        ToolTip.SetTip(FolderText, folder);
        LastSync.Text = s.LastSyncAt is { } at ? Format.Moment(at, DateTimeOffset.Now) : "Not yet";
        ConnectionText.Text = s.ConnectionText ?? "Connecting…";
        VersionText.Text = s.Server is null ? "–" : s.Server.ServerVersion is { Length: > 0 } v ? "Version " + v : "Old version";
        VersionButton.IsVisible = s.Server is not null;
        VersionButton.Content = s.ServerUpdateButtonText;
        VersionTile.Classes.Set("warn", s.ServerIsOlder);
        SpaceText.Text = s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) : "–";
        SpaceTile.Classes.Set("warn", s.ServerSpaceLow);

        TransferCard.IsVisible = s.IsTransferring;
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            TransferSpeed.Text = s.SpeedText;
            LimitPill.IsVisible = s.LimitText is not null;
            LimitText.Text = s.LimitText ?? string.Empty;
            TransferProgress.IsIndeterminate = s.OverallPercent is null;
            TransferProgress.Value = s.OverallPercent ?? 0;
            TransferOverall.Text = s.OverallText;
            var several = s.Active.Count > 1 || s.FilesTotal > 1;
            ActiveDivider.IsVisible = several && s.Active.Count > 0;
            ActiveList.IsVisible = several;
            LiveLists.Sync(_active, s.Active);
            var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.Stroke = Visuals.Resource<IBrush>(uploading ? "S.Green" : "S.Blue");
            TransferArrow.Classes.Set("rise", uploading);
            TransferArrow.Classes.Set("fall", !uploading);
        }

        // Waiting for the other computer's big batch.
        WaitPanel.IsVisible = s.IsWaiting;
        if (s.IsWaiting)
        {
            WaitProgress.IsIndeterminate = s.WaitingPercent is null;
            WaitProgress.Value = s.WaitingPercent ?? 0;
            WaitText.Text = s.WaitingProgressText;
        }
    }

    /// <summary>The Devices page: rebuilt when the server sends a new list.</summary>
    private void ShowDevices(StatusSnapshot s)
    {
        if (s.DevicesUnsupported)
            DevicesEmptyText.Text = "Update the server to see the computers that use it.";
        if (ReferenceEquals(s.Devices, _shownDevices) || s.Devices is null)
            return;
        _shownDevices = s.Devices;
        _devices.Clear();
        foreach (var row in DeviceRow.From(s.Devices, ThisDevice, DateTimeOffset.UtcNow))
            _devices.Add(row);
        if (s.Devices.Count == 0 && !s.DevicesUnsupported)
            DevicesEmptyText.Text = "No computers have used this server yet.";
        UpdatePanels();
    }

    /// <summary>A soft wash of the status colour across the top of the status card.</summary>
    private void Tint(string brushKey)
    {
        if (_tintKey == brushKey)
            return;
        _tintKey = brushKey;
        var color = (Visuals.Resource<IBrush>(brushKey) as ISolidColorBrush)?.Color ?? Colors.Gray;
        HeroTint.Background = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x24, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0.75),
            },
        };
    }

    /// <summary>
    /// Shows or hides the app update card. <paramref name="progress"/> 0–100 while downloading, null otherwise.
    /// </summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        UpdateCard.IsVisible = title is not null;
        if (title is null)
            return;
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateButton.Content = button;
        UpdateButton.IsEnabled = !busy;
        UpdateLater.IsVisible = !busy;
        UpdateProgress.IsVisible = progress is not null;
        UpdateProgress.Value = progress ?? 0;
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activityItems = items;
        LiveLists.Sync(_recent, items.Take(5).ToList());
        if (Page == MainPage.Activity)
            LiveLists.Sync(_activityRows, ActivityDays.Rows(items, DateTimeOffset.Now));
        UpdatePanels();
    }

    /// <summary>
    /// Motion for the status badge: the sync glyph turns while syncing, the badge breathes while
    /// waiting for the other computer, and it pops once whenever the state changes.
    /// </summary>
    private void Animate(StatusSnapshot s)
    {
        StatusGlyph.Classes.Set("spin", s.Status == Pairnets.Core.Sync.RunnerStatus.Syncing && !s.IsWaiting);
        StatusBadgeHost.Classes.Set("pulse", s.IsWaiting);
        var state = s.IsWaiting ? "waiting" : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
        {
            StatusBadgeHost.Classes.Remove("pop");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusBadgeHost.Classes.Add("pop"), Avalonia.Threading.DispatcherPriority.Background);
        }
        _shownState = state;
    }

    private void OnAddComputer(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _actions?.AddComputer();

    private void OnManageDevices(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _actions?.ManageDevices();

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionBadge.IsVisible = items.Count > 0;
        AttentionCount.Text = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AttentionCallout.IsVisible = items.Count > 0;
        AttentionCalloutText.Text = items.Count == 1 ? "1 thing needs your attention" : $"{items.Count} things need your attention";
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        ActivityPanel.IsVisible = _activityItems.Count > 0;
        ActivityEmpty.IsVisible = _activityItems.Count == 0;
        RecentEmpty.IsVisible = _recent.Count == 0;
        AttentionList.IsVisible = _attention.Count > 0;
        AttentionEmpty.IsVisible = _attention.Count == 0;
        DevicesPanel.IsVisible = _devices.Count > 0;
        DevicesEmpty.IsVisible = _devices.Count == 0;
    }

    // ------------------------------------------------------------------ history

    /// <summary>Reads the server's file list (deleted files included) for the History page.</summary>
    internal async Task LoadHistoryAsync()
    {
        var source = _actions?.History;
        if (source is null)
        {
            ShowHistoryMessage("Not connected to the server. History appears here once Pairnets is connected.", retry: false);
            return;
        }
        ShowHistoryMessage("Loading the files on the server…", retry: false);
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
                    HistorySearch.Text = target.Name;
                }
                finally
                {
                    _navigating = false;
                }
            }
            FilterHistory();
            if (_selectAfterLoad is { } selected)
                FileList.SelectedItem = _shownFiles.FirstOrDefault(f => f.Path == selected);
            _selectAfterLoad = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowHistoryMessage("Could not read the files on the server: " + ex.Message, retry: true);
        }
    }

    /// <summary>Opens History on one file (from the activity list).</summary>
    public void ShowVersionsFor(string path)
    {
        _selectAfterLoad = path;
        if (Page == MainPage.History)
            _ = LoadHistoryAsync();
        else
        {
            _serverFiles = null; // read fresh, so the file is there
            Navigate(MainPage.History);
        }
    }

    private void ShowHistoryMessage(string text, bool retry)
    {
        _shownFiles.Clear();
        FileList.IsVisible = false;
        HistoryMessage.IsVisible = true;
        HistoryMessageText.Text = text;
        HistoryRetry.IsVisible = retry;
        HistorySummary.Text = string.Empty;
    }

    private void FilterHistory()
    {
        if (_serverFiles is null)
            return;
        var deleted = _deletedMode;
        var search = HistorySearch.Text;
        var (rows, total) = HistoryQuery.Filter(_serverFiles, deleted, search, DateTimeOffset.UtcNow);
        var selected = (FileList.SelectedItem as ServerFile)?.Path;
        _shownFiles.Clear();
        foreach (var row in rows)
            _shownFiles.Add(row);
        HistorySummary.Text = HistoryQuery.Summary(rows.Count, total, deleted, !string.IsNullOrWhiteSpace(search));
        FileList.IsVisible = rows.Count > 0;
        HistoryMessage.IsVisible = rows.Count == 0;
        HistoryRetry.IsVisible = false;
        HistoryMessageIcon.Data = Visuals.Resource<Geometry>(deleted ? "I.Trash" : "I.File");
        HistoryMessageText.Text = HistorySummary.Text;
        if (selected is not null)
            FileList.SelectedItem = _shownFiles.FirstOrDefault(f => f.Path == selected);
        if (FileList.SelectedItem is null)
            ShowDetail(null);
    }

    private void OnHistoryModeChanged(object? sender, RoutedEventArgs e)
    {
        // The newly checked tab reports before the other one unchecks, so go by which one it is.
        if (sender is not RadioButton { IsChecked: true } tab || _navigating)
            return;
        _deletedMode = tab == DeletedTab;
        FilterHistory();
    }

    private void OnHistorySearch(object? sender, TextChangedEventArgs e)
    {
        if (!_navigating)
            FilterHistory();
    }

    private void OnHistoryReload(object? sender, RoutedEventArgs e) => _ = LoadHistoryAsync();

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e) => ShowDetail(FileList.SelectedItem as ServerFile);

    private async void ShowDetail(ServerFile? file)
    {
        _versionsCts?.Cancel();
        DetailEmpty.IsVisible = file is null;
        DetailPanel.IsVisible = file is not null;
        _versions.Clear();
        RestoreMessage.IsVisible = false;
        if (file is null)
            return;
        DetailName.Text = file.Name;
        DetailFolder.Text = file.Folder;
        var now = DateTimeOffset.UtcNow;
        DetailState.Text = file.Deleted
            ? $"Deleted {Format.Moment(file.ChangedAt, now)}"
            : $"Current version · {Format.Bytes(file.Size)} · changed {Format.Moment(file.ChangedAt, now)}";
        DetailStatePill.Classes.Set("warn", file.Deleted);
        VersionsMessage.IsVisible = true;
        VersionsMessage.Text = "Loading versions…";
        if (_actions?.History is not { } source)
            return;
        var cts = _versionsCts = new CancellationTokenSource();
        try
        {
            var versions = await source.GetVersionsAsync(file.Path, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var row in VersionRow.For(file, versions, DateTimeOffset.UtcNow))
                _versions.Add(row);
            VersionsMessage.IsVisible = _versions.Count == 0;
            VersionsMessage.Text = file.Deleted
                ? "The server no longer has a copy: it was deleted more than 30 days ago."
                : "No older versions: this file has not been changed or replaced in the last 30 days.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                VersionsMessage.Text = "Could not read the versions: " + ex.Message;
        }
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VersionRow row } button)
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

    /// <summary>Restores one version, says how it went, and re-reads the list (the file is current again).</summary>
    internal async Task RestoreAsync(VersionRow row)
    {
        if (_actions?.History is not { } source)
            return;
        var error = await source.RestoreVersionAsync(row.Path, row.Version, CancellationToken.None);
        var message = error ?? $"Restored. {Pairnets.Core.Paths.PathRules.FileName(row.Path)} is back on your computers in a few seconds.";
        if (error is null)
        {
            _selectAfterLoad = row.Path;
            await LoadHistoryAsync(); // it is a current file again, with one more version
        }
        RestoreMessage.IsVisible = true;
        RestoreMessage.Classes.Set("ok", error is null);
        RestoreMessage.Classes.Set("bad", error is not null);
        RestoreMessageText.Text = message;
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Headline.Text ?? string.Empty;
    internal bool FixVisible => FixButton.IsVisible;
    internal int ActivityCount => _activityItems.Count;
    internal int ActivityRowCount => _activityRows.Count;
    internal bool TransferVisible => TransferCard.IsVisible;
    internal bool GlyphSpins => StatusGlyph.Classes.Contains("spin");
    internal bool BadgePulses => StatusBadgeHost.Classes.Contains("pulse");
    internal string ArrowMotion => TransferArrow.Classes.Contains("rise") ? "rise" : TransferArrow.Classes.Contains("fall") ? "fall" : "none";
    internal int ActiveRows => _active.Count;
    internal string MapOtherText => Map.OtherText;
    internal bool MapHereFlowing => Map.HereFlowing;
    internal int HistoryRows => _shownFiles.Count;
    internal int VersionRows => _versions.Count;
    internal string RestoreText => RestoreMessage.IsVisible ? RestoreMessageText.Text ?? string.Empty : string.Empty;
    internal bool CalloutVisible => AttentionCallout.IsVisible;
    internal int DeviceRows => _devices.Count;
    internal bool DevicesShown => DevicesPanel.IsVisible && DevicesPage.IsVisible;

    /// <summary>Switches to the Devices page (used by the headless UI test).</summary>
    internal void ShowDevicesTab() => Navigate(MainPage.Devices);

    internal void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    internal void SearchHistory(string text) => HistorySearch.Text = text;

    internal void ShowAllFiles() => FilesTab.IsChecked = true;

    internal Task RestoreFirstVersionAsync() => _versions.Count == 0 ? Task.CompletedTask : RestoreAsync(_versions[0]);

    /// <summary>Clicks a sidebar entry, as a person would.</summary>
    internal void ClickNav(MainPage page) => NavFor(page).IsChecked = true;

    internal bool SettingsShown => SettingsPage.Content is not null;

    private void OnFix(object? sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object? sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object? sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnUpdateNow(object? sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object? sender, RoutedEventArgs e) => _actions?.DismissUpdate();
    private void OnDownloadNow(object? sender, RoutedEventArgs e) => _actions?.DownloadNow();
    private void OnUpdateServer(object? sender, RoutedEventArgs e) => _actions?.UpdateServer();
    private void OnSeeAllActivity(object? sender, RoutedEventArgs e) => Navigate(MainPage.Activity);
    private void OnReviewAttention(object? sender, RoutedEventArgs e) => Navigate(MainPage.Attention);

    private void OnRevealActivity(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityItem { Path: { } path } })
            _actions?.RevealFile(path);
    }

    private void OnActivityVersions(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityItem { Path: { } path } })
            ShowVersionsFor(path);
    }

    private void OnAttentionAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: AttentionItem item })
            item.Action();
    }
}
