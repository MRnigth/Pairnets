using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

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

/// <summary>What the main window's buttons do (implemented by <see cref="TrayController"/>).</summary>
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

    /// <summary>Shows a synced file in Explorer (or the folder, when the file is gone).</summary>
    void RevealFile(string syncPath);

    /// <summary>Where the History page reads from; null while not connected.</summary>
    IHistorySource? History { get; }
}

/// <summary>
/// The main window: a sidebar with Overview (status, the two computers, facts, recent activity),
/// Activity, History, Needs attention and Settings. It only displays what <see cref="ClientSession"/>
/// reports and forwards clicks. Same design as the Mac/Linux app.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<object> _activityRows = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<ServerFile> _shownFiles = [];
    private readonly ObservableCollection<VersionRow> _versions = [];
    private readonly ObservableCollection<DeviceRowView> _devices = [];
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private IReadOnlyList<ServerFile>? _serverFiles;
    private DateTimeOffset _serverFilesAt;
    private CancellationTokenSource? _versionsCts;
    private string? _selectAfterLoad;
    private string? _folder;
    private OverviewModel? _overview;
    private bool _navigating;
    private bool _deletedMode = true;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private SideUpdate _update = SideUpdate.None;
    private int _attentionCount;

    private static readonly TimeSpan HistoryFreshFor = TimeSpan.FromMinutes(1);

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        Overview.Act += OnOverviewAction;
        Overview.SeeAllActivity += () => Navigate(MainPage.Activity);
        Overview.MoreComputers += () => Navigate(MainPage.Devices);
        ActivityList.ItemsSource = _activityRows;
        AttentionList.ItemsSource = _attention;
        FileList.ItemsSource = _shownFiles;
        VersionList.ItemsSource = _versions;
        DevicesList.ItemsSource = _devices;
        AppIcon.Source = AppIcons.Large;
        DockIcon.Source = AppIcons.Large;
        RefreshSide();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopMotion(); // nothing moves (or costs CPU) while Pairnets sits in the tray
        };
        UpdatePanels();
    }

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainPage Page { get; private set; } = MainPage.Overview;

    /// <summary>This computer's name, marked in the Devices list and shown first in the overview's picture.</summary>
    public string? ThisDevice { get; set; }

    /// <summary>Show the MB/s on the overview picture's lines (Settings: "Show speed on the map").</summary>
    public bool ShowMapSpeed { get; set; } = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; // keep running in the tray
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
            DockFor(page).IsChecked = true;
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

    private RadioButton DockFor(MainPage page) => page switch
    {
        MainPage.Activity => DockActivity,
        MainPage.History => DockHistory,
        MainPage.Devices => DockDevices,
        MainPage.Attention => DockAttention,
        MainPage.Settings => DockSettings,
        _ => DockOverview,
    };

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav || _navigating || PageTitle is null)
            return; // Checked also fires during InitializeComponent
        var page = nav == NavActivity || nav == DockActivity ? MainPage.Activity
            : nav == NavHistory || nav == DockHistory ? MainPage.History
            : nav == NavDevices || nav == DockDevices ? MainPage.Devices
            : nav == NavAttention || nav == DockAttention ? MainPage.Attention
            : nav == NavSettings || nav == DockSettings ? MainPage.Settings
            : MainPage.Overview;
        // The tile and the dock button of a page are one choice.
        _navigating = true;
        try
        {
            NavFor(page).IsChecked = true;
            DockFor(page).IsChecked = true;
        }
        finally
        {
            _navigating = false;
        }
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
        PageSubtitle.Visibility = Show(PageSubtitle.Text.Length > 0);
        foreach (var (element, shown) in new (FrameworkElement, MainPage)[]
                 {
                     (OverviewPage, MainPage.Overview), (ActivityPage, MainPage.Activity), (HistoryPage, MainPage.History),
                     (DevicesPage, MainPage.Devices), (AttentionPage, MainPage.Attention), (SettingsPage, MainPage.Settings),
                 })
        {
            var visible = shown == page;
            if (visible && element.Visibility != Visibility.Visible)
                Motion.Enter(element);
            element.Visibility = Show(visible);
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
    public void ShowSettingsPage(FrameworkElement form)
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
        if (folder is not null)
            _folder = folder;
        _status = s;
        RefreshSide();
        ShowDevices(s);
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        RefreshOverview();
    }

    /// <summary>Draws the Overview from the last status, activity and attention list (each of them calls this when it changes).</summary>
    private void RefreshOverview()
    {
        if (Overview is null)
            return; // Checked fires during InitializeComponent
        var now = DateTimeOffset.UtcNow;
        Overview.ShowSpeed = ShowMapSpeed;
        _overview = OverviewModel.Build(_status, ThisDevice, _folder, _attentionCount, _activityItems, now);
        Overview.Show(_overview, NestMapModel.Build(_status, ThisDevice, now), ThisDevice);
    }

    private void OnOverviewAction(OverviewAction action)
    {
        switch (action)
        {
            case OverviewAction.Fix:
                _actions?.FixBlocked();
                break;
            case OverviewAction.SyncNow:
                _actions?.SyncNow();
                break;
            case OverviewAction.TogglePause:
                _actions?.TogglePause();
                break;
            case OverviewAction.OpenFolder:
                _actions?.OpenFolder();
                break;
            case OverviewAction.DownloadNow:
                _actions?.DownloadNow();
                break;
            case OverviewAction.CheckNestUpdate:
                _actions?.UpdateServer();
                break;
            case OverviewAction.OpenAccount:
                OnAccount(this, new RoutedEventArgs());
                break;
            case OverviewAction.SeeList:
            case OverviewAction.Review:
                Navigate(MainPage.Attention);
                break;
            case OverviewAction.SeeActivity:
                Navigate(MainPage.Activity);
                break;
        }
    }

    /// <summary>The Devices page: the server's list, with what each computer moves right now (updated in place).</summary>
    private void ShowDevices(StatusSnapshot s)
    {
        if (s.DevicesUnsupported)
            DevicesEmptyText.Text = "Update the server to see the computers that use it.";
        if (s.Devices is null)
            return;
        var count = _devices.Count;
        LiveLists.Sync(_devices, DeviceRow.From(s.Devices, ThisDevice, DateTimeOffset.UtcNow, s));
        if (s.Devices.Count == 0 && !s.DevicesUnsupported)
            DevicesEmptyText.Text = "No computers have used this server yet.";
        if (count != _devices.Count)
            UpdatePanels();
    }

    /// <summary>Shows or hides the slim app update card. <paramref name="progress"/> 0–100 while downloading.</summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        _update = SideUpdate.From(title, detail, button, progress, busy);
        UpdateCard.Visibility = Show(_update.Visible);
        Motion.Bob(UpdateIcon, _update.Visible && !busy);
        if (!_update.Visible)
            return;
        UpdateTitle.Text = _update.Title;
        UpdateTitle.ToolTip = detail.Length > 0 ? detail : null;
        UpdateButton.Content = button;
        UpdateButton.Visibility = Show(!busy);
        UpdateLater.Visibility = Show(!busy);
        UpdatePercent.Visibility = Show(busy);
        UpdatePercent.Text = _update.PercentText;
        UpdateProgress.Visibility = Show(_update.ShowBar);
        Motion.Glide(UpdateProgress, progress ?? 0);
    }

    /// <summary>The longer words behind the update card's title (its tooltip), for the tests.</summary>
    public string UpdateDetailText => UpdateTitle.ToolTip as string ?? string.Empty;

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activityItems = items;
        RefreshSide();
        RefreshOverview();
        if (Page == MainPage.Activity)
            LiveLists.Sync(_activityRows, ActivityDays.Rows(items, DateTimeOffset.Now));
        UpdatePanels();
    }


    private void StopMotion()
    {
        Overview.StopMotion();
        Motion.Bob(UpdateIcon, false);
    }

    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement row)
            Motion.Enter(row);
    }

    private void OnAddComputer(object sender, RoutedEventArgs e) => _actions?.AddComputer();

    private void OnManageDevices(object sender, RoutedEventArgs e) => _actions?.ManageDevices();

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        _attentionCount = items.Count;
        RefreshSide();
        RefreshOverview();
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (ActivityPanel is null)
            return; // Checked fires during InitializeComponent
        ActivityPanel.Visibility = Show(_activityItems.Count > 0);
        ActivityEmpty.Visibility = Show(_activityItems.Count == 0);
        AttentionList.Visibility = Show(_attention.Count > 0);
        AttentionEmpty.Visibility = Show(_attention.Count == 0);
        DevicesPanel.Visibility = Show(_devices.Count > 0);
        DevicesEmpty.Visibility = Show(_devices.Count == 0);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ history

    /// <summary>Reads the server's file list (deleted files included) for the History page.</summary>
    public async Task LoadHistoryAsync()
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
        FileList.Visibility = Visibility.Collapsed;
        HistoryMessage.Visibility = Visibility.Visible;
        HistoryMessageText.Text = text;
        HistoryRetry.Visibility = Show(retry);
        HistorySummary.Text = string.Empty;
    }

    private void FilterHistory()
    {
        if (_serverFiles is null)
            return;
        var search = HistorySearch.Text;
        var (rows, total) = HistoryQuery.Filter(_serverFiles, _deletedMode, search, DateTimeOffset.UtcNow);
        var selected = (FileList.SelectedItem as ServerFile)?.Path;
        _shownFiles.Clear();
        foreach (var row in rows)
            _shownFiles.Add(row);
        HistorySummary.Text = HistoryQuery.Summary(rows.Count, total, _deletedMode, !string.IsNullOrWhiteSpace(search));
        FileList.Visibility = Show(rows.Count > 0);
        HistoryMessage.Visibility = Show(rows.Count == 0);
        HistoryRetry.Visibility = Visibility.Collapsed;
        HistoryMessageIcon.Data = Visuals.Resource<Geometry>(_deletedMode ? "I.Trash" : "I.File");
        HistoryMessageText.Text = HistorySummary.Text;
        if (selected is not null)
            FileList.SelectedItem = _shownFiles.FirstOrDefault(f => f.Path == selected);
        if (FileList.SelectedItem is null)
            ShowDetail(null);
    }

    private void OnHistoryModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || _navigating || FileList is null)
            return; // Checked also fires during InitializeComponent
        _deletedMode = tab == DeletedTab;
        FilterHistory();
    }

    private void OnHistorySearch(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Show(HistorySearch.Text.Length == 0);
        if (!_navigating)
            FilterHistory();
    }

    private void OnHistoryReload(object sender, RoutedEventArgs e) => _ = LoadHistoryAsync();

    private void OnFileSelected(object sender, SelectionChangedEventArgs e) => ShowDetail(FileList.SelectedItem as ServerFile);

    private async void ShowDetail(ServerFile? file)
    {
        _versionsCts?.Cancel();
        DetailEmpty.Visibility = Show(file is null);
        DetailPanel.Visibility = Show(file is not null);
        _versions.Clear();
        RestoreMessage.Visibility = Visibility.Collapsed;
        if (file is null)
            return;
        DetailName.Text = file.Name;
        DetailFolder.Text = file.Folder;
        var now = DateTimeOffset.UtcNow;
        DetailState.Text = file.Deleted
            ? $"Deleted {Format.Moment(file.ChangedAt, now)}"
            : $"Current version · {Format.Bytes(file.Size)} · changed {Format.Moment(file.ChangedAt, now)}";
        DetailStatePill.SetResourceReference(Border.BackgroundProperty, file.Deleted ? "T.WarnPill" : "T.Pill");
        DetailState.SetResourceReference(TextBlock.ForegroundProperty, file.Deleted ? "T.WarnText" : "T.Text");
        VersionsMessage.Visibility = Visibility.Visible;
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
            VersionsMessage.Visibility = Show(_versions.Count == 0);
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

    /// <summary>Restores one version, says how it went, and re-reads the list (the file is current again).</summary>
    public async Task RestoreAsync(VersionRow row)
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
        RestoreMessage.Visibility = Visibility.Visible;
        RestoreMessage.SetResourceReference(Border.BackgroundProperty, error is null ? "T.OkPill" : "T.BadPill");
        RestoreMessageText.SetResourceReference(TextBlock.ForegroundProperty, error is null ? "T.OkText" : "T.BadText");
        RestoreMessageText.Text = message;
    }

    /// <summary>Selects a row in the History list (the screenshot tool).</summary>
    public void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    /// <summary>What the overview's picture says about the other computer (checks).</summary>
    public string MapOtherText => Overview.OtherText;

    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnUpdateNow(object sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object sender, RoutedEventArgs e) => _actions?.DismissUpdate();

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.IsOpen = true;
        }
    }

    private void OnRevealActivity(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivityItem { Path: { } path } })
            _actions?.RevealFile(path);
    }

    private void OnActivityVersions(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivityItem { Path: { } path } })
            ShowVersionsFor(path);
    }

    private void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AttentionItem item })
            item.Action();
    }
}
