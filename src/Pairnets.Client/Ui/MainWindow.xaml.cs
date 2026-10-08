using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>An item in the "Needs attention" list with one action button.</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action);

/// <summary>The pages of the main window (the rail, plus Account from the account button).</summary>
public enum MainPage
{
    Overview,
    Activity,
    History,
    Devices,
    Attention,
    Settings,
    Account,
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

    /// <summary>"Add a computer…": how to add one (it signs in on the new computer).</summary>
    void AddComputer();

    /// <summary>Opens the nest's Devices page (rename, remove, approve).</summary>
    void ManageDevices();

    /// <summary>Opens the nest's website.</summary>
    void OpenNest();

    /// <summary>"Sign out of this computer…" (asks first).</summary>
    void SignOut();

    /// <summary>"Reset this app…" (asks first).</summary>
    void ResetEverything();

    /// <summary>A notification switch on the Account page changed.</summary>
    void SetNotify(NoticeKind kind, bool on);

    /// <summary>Shows a synced file in Explorer (or the folder, when the file is gone).</summary>
    void RevealFile(string syncPath);

    /// <summary>Where the History page reads from; null while not connected.</summary>
    IHistorySource? History { get; }
}

/// <summary>
/// The main window: an icon rail with Overview (the ring and the status in words, the computers on one
/// line, what is moving, recent activity, facts), Activity, History, Devices, Needs attention, Settings,
/// and the Account page behind the account button. It only displays what <see cref="ClientSession"/>
/// reports and forwards clicks. Same design as the Mac/Linux app.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<object> _recent = [];
    private readonly ObservableCollection<object> _activityRows = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<TransferRowView> _active = [];
    private readonly ObservableCollection<ServerFile> _shownFiles = [];
    private readonly ObservableCollection<VersionRow> _versions = [];
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private IReadOnlyList<DeviceInfo>? _shownDevices;
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private IReadOnlyList<ServerFile>? _serverFiles;
    private DateTimeOffset _serverFilesAt;
    private CancellationTokenSource? _versionsCts;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private string? _selectAfterLoad;
    private string? _shownState;
    private RingKind _ringKind = RingKind.Offline;
    private bool _ringSpins;
    private bool _ringBreathes;
    private bool _updateBobs;
    private bool _navigating;
    private bool _settingNotify;
    private bool _deletedMode = true;
    private int _recentCount = 5;

    private static readonly TimeSpan HistoryFreshFor = TimeSpan.FromMinutes(1);

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        RecentList.ItemsSource = _recent;
        ActivityList.ItemsSource = _activityRows;
        AttentionList.ItemsSource = _attention;
        ActiveList.ItemsSource = _active;
        FileList.ItemsSource = _shownFiles;
        VersionList.ItemsSource = _versions;
        DevicesList.ItemsSource = _devices;
        AppVersion.Text = "Pairnets " + PairnetsInfo.ProductVersion + " · " + PairnetsInfo.Copyright;
        AppIcon.Source = AppIcons.Large;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                StartMotion();
            else
                StopMotion(); // nothing moves (or costs CPU) while Pairnets sits in the tray
        };
        ShowPage(MainPage.Overview);
        ShowAccount();
        UpdatePanels();
    }

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainPage Page { get; private set; } = MainPage.Overview;

    /// <summary>This computer's name, marked in the Devices list and shown first in the overview's line.</summary>
    public string? ThisDevice { get; set; }

    /// <summary>The server address in the settings (the Account page falls back to it when the nest has no website).</summary>
    public string? ServerAddress { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PopoverLayer.Visibility == Visibility.Visible)
        {
            ClosePopovers();
            e.Handled = true;
        }
        base.OnKeyDown(e);
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
        MainPage.Account => NavAccount,
        _ => NavOverview,
    };

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav || _navigating || PageTitle is null)
            return; // Checked also fires during InitializeComponent
        var page = nav == NavActivity ? MainPage.Activity
            : nav == NavHistory ? MainPage.History
            : nav == NavDevices ? MainPage.Devices
            : nav == NavAttention ? MainPage.Attention
            : nav == NavSettings ? MainPage.Settings
            : nav == NavAccount ? MainPage.Account
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
        ClosePopovers();
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            MainPage.Activity => ("Activity", "What synced recently, newest first. Right-click a file for more."),
            MainPage.History => ("History", "Get back deleted files and older versions. The server keeps them for 30 days."),
            MainPage.Devices => ("Devices", "Every computer that uses your server: online now, or when it was last seen."),
            MainPage.Attention => ("Needs attention", "Things Pairnets can't decide for you."),
            MainPage.Settings => ("Settings", "Your server, the folder, and how Pairnets behaves."),
            MainPage.Account => ("Account", "Who you're signed in as, and where your files live."),
            _ => ("Overview", string.Empty),
        };
        // The overview's title is small: the status underneath is the real headline.
        var overview = page == MainPage.Overview;
        PageTitle.FontSize = overview ? 15 : 26;
        if (overview)
            PageTitle.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
        else
            PageTitle.ClearValue(TextBlock.ForegroundProperty);
        // Only the overview has the "needs your attention" pill (on the other pages it would push the buttons out).
        AttentionCallout.Visibility = Show(overview && _attention.Count > 0);
        PageSubtitle.Visibility = Show(PageSubtitle.Text.Length > 0);
        foreach (var (element, shown) in new (FrameworkElement, MainPage)[]
                 {
                     (OverviewPage, MainPage.Overview), (ActivityPage, MainPage.Activity), (HistoryPage, MainPage.History),
                     (DevicesPage, MainPage.Devices), (AttentionPage, MainPage.Attention), (SettingsPage, MainPage.Settings),
                     (AccountPage, MainPage.Account),
                 })
        {
            var visible = shown == page;
            if (visible && element.Visibility != Visibility.Visible)
                Motion.Enter(element);
            element.Visibility = Show(visible);
        }
        if (page == MainPage.Activity)
            LiveLists.Sync(_activityRows, ActivityDays.GroupedRows(_activityItems, DateTimeOffset.Now));
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
        device ??= ThisDevice;
        _status = s;
        ShowDevices(s);
        ShowRing(s);
        Headline.Text = s.Headline;
        var lines = OverviewLines.From(s, DateTimeOffset.Now);
        Lead.Text = lines.Lead;
        Lead.Visibility = Show(lines.Lead.Length > 0);
        DetailLines.ItemsSource = lines.Lines;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.Visibility = Show(s.FixLabel is not null);
        FixButton.Content = s.FixLabel;
        WaitPanel.Visibility = Show(s.IsWaiting);
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        // Facts
        FolderText.Text = folder ?? "Not chosen yet";
        FolderText.ToolTip = folder;
        LastSync.Text = s.LastSyncAt is { } at ? Format.Moment(at, DateTimeOffset.Now) : "Not yet";
        VersionText.Text = s.Server is null ? "–"
            : (s.Server.ServerVersion is { Length: > 0 } v ? "Version " + v : "Old version") + (s.ConnectionText is { } c ? " · " + c : string.Empty);
        VersionButton.Visibility = Show(s.Server is not null && s.ServerIsOlder);
        VersionButton.Content = s.ServerUpdateButtonText;
        Warn(VersionText, s.ServerIsOlder);
        SpaceText.Text = s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " on the server" : "–";
        Warn(SpaceText, s.ServerSpaceLow);

        // Fewer recent rows when the status above takes more room, so the facts stay in view.
        var recentCount = s.IsTransferring || s.IsWaiting ? 4 : 5;
        if (recentCount != _recentCount)
        {
            _recentCount = recentCount;
            LiveLists.Sync(_recent, ActivityGroups.Recent(_activityItems, _recentCount));
            UpdatePanels();
        }

        // In progress: files and folders of this sync; recent activity takes the whole width otherwise.
        TransferCard.Visibility = Show(s.IsTransferring);
        Grid.SetColumn(RecentPanel, s.IsTransferring ? 2 : 0);
        Grid.SetColumnSpan(RecentPanel, s.IsTransferring ? 1 : 3);
        var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
        if (s.IsTransferring)
            LiveLists.Sync(_active, TransferGroups.Build(s));
        else
            _active.Clear();
        Map.ShowSpeed(s.IsTransferring ? Format.Speed(s.BytesPerSecond) : null, uploading, s.IsTransferring && s.BytesPerSecond >= 1);
        Map.ShowOtherNote(s.IsWaiting ? s.WaitingFor!.Device + " uploading" : null);
        ShowAccount();
    }

    /// <summary>A fact in the warning colour (an old server, little space left).</summary>
    private static void Warn(TextBlock value, bool warn)
    {
        if (warn)
            value.SetResourceReference(TextBlock.ForegroundProperty, "T.WarnText");
        else
            value.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>The big ring: how far, in which colour, with a number or an icon in the middle.</summary>
    private void ShowRing(StatusSnapshot s)
    {
        var ring = OverviewRing.From(s);
        _ringKind = ring.Kind;
        Ring.SetResourceReference(ProgressRing.RingBrushProperty, ring.BrushKey);
        Ring.GlideTo(ring.Spins ? 25 : ring.Percent);
        _ringSpins = ring.Spins;
        RingText.Text = ring.CenterText ?? string.Empty;
        RingText.Visibility = Show(ring.CenterText is not null);
        RingCaption.Text = ring.Caption ?? string.Empty;
        RingCaption.Visibility = Show(ring.Caption is not null);
        RingIcon.Visibility = Show(ring.CenterText is null && ring.IconKey is not null && !ring.Spins);
        if (ring.IconKey is not null)
            RingIcon.Data = Visuals.Resource<Geometry>(ring.IconKey);
        RingIcon.SetResourceReference(LineIcon.StrokeProperty, ring.BrushKey);
        _ringBreathes = ring.Breathes;
        if (IsVisible)
        {
            Motion.Spin(Ring, _ringSpins);
            Motion.Pulse(RingHost, _ringBreathes);
        }

        // One small pop whenever the state changes (not while waiting: that breathes instead).
        var state = s.IsWaiting ? "waiting" : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
            Motion.Pop(RingHost);
        _shownState = state;
    }

    private void StartMotion()
    {
        Motion.Spin(Ring, _ringSpins);
        Motion.Pulse(RingHost, _ringBreathes);
        Motion.Bob(UpdateIcon, _updateBobs);
    }

    private void StopMotion()
    {
        Motion.Spin(Ring, false);
        Motion.Pulse(RingHost, false);
        Motion.Bob(UpdateIcon, false);
        _shownState = null;
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

    /// <summary>The account button, its menu and the Account page.</summary>
    private void ShowAccount()
    {
        var a = AccountSummary.Build(_status, ThisDevice, ServerAddress);
        AccountInitial.Text = AccountMenuInitial.Text = AccountPageInitial.Text = a.Initial;
        AccountMenuTitle.Text = AccountPageTitle.Text = a.Title;
        AccountMenuSubtitle.Text = AccountPageSubtitle.Text = a.Subtitle;
        AccountMenuDevice.Text = a.DeviceName;
        AccountMenuNest.Text = a.NestHost ?? "–";
        AccountNest.Text = a.NestHost ?? "No nest yet";
        AccountNestDetail.Text = a.NestDetail;
        AccountNestDetail.Visibility = Show(a.NestDetail.Length > 0);
        AccountDevice.Text = a.DeviceName;
        AccountDeviceDetail.Text = a.DeviceDetail;
        AccountButton.ToolTip = a.Title;
    }

    /// <summary>The notification switches on the Account page, as saved.</summary>
    public void ShowNotifySettings(bool joinRequests, bool attention, bool updates)
    {
        _settingNotify = true;
        try
        {
            NotifyJoin.IsChecked = joinRequests;
            NotifyAttention.IsChecked = attention;
            NotifyUpdates.IsChecked = updates;
        }
        finally
        {
            _settingNotify = false;
        }
    }

    private void OnNotifyChanged(object sender, RoutedEventArgs e)
    {
        if (_settingNotify || sender is not CheckBox toggle || NotifyUpdates is null)
            return; // Checked also fires during InitializeComponent
        var kind = toggle == NotifyJoin ? NoticeKind.JoinRequest : toggle == NotifyAttention ? NoticeKind.Attention : NoticeKind.Update;
        _actions?.SetNotify(kind, toggle.IsChecked == true);
    }

    /// <summary>
    /// Shows or hides the app update (a button in the rail that opens a small notice). <paramref name="progress"/>
    /// 0–100 while downloading, null otherwise.
    /// </summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        UpdateRailButton.Visibility = Show(title is not null);
        _updateBobs = title is not null && !busy;
        Motion.Bob(UpdateIcon, _updateBobs && IsVisible);
        if (title is null)
        {
            if (UpdateCard.Visibility == Visibility.Visible)
                ClosePopovers();
            return;
        }
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateButton.Content = button;
        UpdateButton.IsEnabled = !busy;
        UpdateLater.Visibility = Show(!busy);
        UpdateProgress.Visibility = Show(progress is not null);
        Motion.Glide(UpdateProgress, progress ?? 0);
        UpdateRailButton.ToolTip = title;
    }

    /// <summary>Opens the update notice next to the rail (also for screenshots).</summary>
    public void OpenUpdateNotice()
    {
        if (UpdateRailButton.Visibility == Visibility.Visible)
            OpenPopover(UpdateCard, UpdateRailButton);
    }

    /// <summary>Opens the account menu next to the rail (also for screenshots).</summary>
    public void OpenAccountMenu() => OpenPopover(AccountMenu, AccountButton);

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activityItems = items;
        LiveLists.Sync(_recent, ActivityGroups.Recent(items, _recentCount));
        if (Page == MainPage.Activity)
            LiveLists.Sync(_activityRows, ActivityDays.GroupedRows(items, DateTimeOffset.Now));
        UpdatePanels();
    }

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionBadge.Visibility = Show(items.Count > 0);
        AttentionCount.Text = items.Count.ToString(CultureInfo.InvariantCulture);
        AttentionCallout.Visibility = Show(items.Count > 0 && Page == MainPage.Overview);
        AttentionCalloutText.Text = items.Count == 1 ? "1 thing needs your attention" : $"{items.Count} things need your attention";
        NavAttention.ToolTip = items.Count == 0 ? "Needs attention" : $"Needs attention ({items.Count})";
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (ActivityPanel is null)
            return; // Checked fires during InitializeComponent
        ActivityPanel.Visibility = Show(_activityItems.Count > 0);
        ActivityEmpty.Visibility = Show(_activityItems.Count == 0);
        RecentEmpty.Visibility = Show(_recent.Count == 0);
        AttentionListBorder.Visibility = Show(_attention.Count > 0);
        AttentionEmpty.Visibility = Show(_attention.Count == 0);
        DevicesPanel.Visibility = Show(_devices.Count > 0);
        DevicesEmpty.Visibility = Show(_devices.Count == 0);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement row)
            Motion.Enter(row);
    }

    /// <summary>A folder row opened or closed: its chevron turns.</summary>
    private void OnChevronToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && LogicalTreeHelper.FindLogicalNode(toggle, "Chevron") is UIElement chevron)
            Motion.Turn(chevron, toggle.IsChecked == true);
    }

    // ------------------------------------------------------------------ popovers (drawn in the window, so screenshots show them)

    private void OpenPopover(Border popover, Button from)
    {
        var wasOpen = popover.Visibility == Visibility.Visible;
        ClosePopovers();
        if (wasOpen)
            return;
        PopoverLayer.Visibility = Visibility.Visible;
        popover.Visibility = Visibility.Visible;
        Look.SetIsOpen(from, true);
    }

    private void ClosePopovers()
    {
        PopoverLayer.Visibility = Visibility.Collapsed;
        UpdateCard.Visibility = Visibility.Collapsed;
        AccountMenu.Visibility = Visibility.Collapsed;
        Look.SetIsOpen(UpdateRailButton, false);
        Look.SetIsOpen(AccountButton, false);
    }

    private void OnToggleUpdate(object sender, RoutedEventArgs e) => OpenPopover(UpdateCard, UpdateRailButton);

    private void OnToggleAccount(object sender, RoutedEventArgs e) => OpenPopover(AccountMenu, AccountButton);

    /// <summary>A click outside the open menu closes it.</summary>
    private void OnPopoverLayerPressed(object sender, MouseButtonEventArgs e) => ClosePopovers();

    /// <summary>Clicks inside a menu stay there.</summary>
    private void OnPopoverPressed(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnAccountSettings(object sender, RoutedEventArgs e) => Navigate(MainPage.Account);

    private void OnRenameInSettings(object sender, RoutedEventArgs e)
    {
        if (_actions is not null)
            _actions.ShowSettings();
        else
            Navigate(MainPage.Settings);
    }

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
        DetailStatePill.SetResourceReference(StyleProperty, file.Deleted ? "BadPill" : "Pill");
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
        RestoreMessage.SetResourceReference(StyleProperty, error is null ? "OkChip" : "BadChip");
        RestoreMessageText.Text = message;
    }

    /// <summary>Selects a row in the History list (the screenshot tool).</summary>
    public void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    /// <summary>What the overview's line says about the other computer (checks).</summary>
    public string MapOtherText => Map.OtherText;

    /// <summary>Opens the first folder in "In progress" (the screenshot tool).</summary>
    public void ExpandFirstFolder()
    {
        UpdateLayout();
        foreach (var toggle in Descendants<ToggleButton>(ActiveList))
        {
            if (toggle.IsVisible)
            {
                toggle.IsChecked = true;
                return;
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    internal RingKind RingKind => _ringKind;
    internal bool UpdateNoticeShown => UpdateCard.Visibility == Visibility.Visible;
    internal bool AccountMenuShown => AccountMenu.Visibility == Visibility.Visible;
    internal bool AttentionEmptyShown => AttentionEmpty.Visibility == Visibility.Visible;

    private void OnFix(object sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnDownloadNow(object sender, RoutedEventArgs e) => _actions?.DownloadNow();
    private void OnUpdateServer(object sender, RoutedEventArgs e) => _actions?.UpdateServer();
    private void OnSeeAllActivity(object sender, RoutedEventArgs e) => Navigate(MainPage.Activity);
    private void OnReviewAttention(object sender, RoutedEventArgs e) => Navigate(MainPage.Attention);
    private void OnAddComputer(object sender, RoutedEventArgs e) => _actions?.AddComputer();
    private void OnOpenNest(object sender, RoutedEventArgs e) => _actions?.OpenNest();
    private void OnUpdateNow(object sender, RoutedEventArgs e) => _actions?.UpdateNow();

    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.DismissUpdate();
    }

    private void OnManageDevices(object sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.ManageDevices();
    }

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.SignOut();
    }

    private void OnResetApp(object sender, RoutedEventArgs e) => _actions?.ResetEverything();

    /// <summary>The "…" menu opens under the button, its right edge lined up with the button's.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 10, target.Height + 4), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
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
