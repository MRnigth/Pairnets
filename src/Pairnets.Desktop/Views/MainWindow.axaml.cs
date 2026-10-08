using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

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

    /// <summary>Shows a synced file in the file manager (or the folder, when the file is gone).</summary>
    void RevealFile(string syncPath);

    /// <summary>Where the History page reads from; null while not connected.</summary>
    IHistorySource? History { get; }
}

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
    private bool _navigating;
    private bool _settingNotify;
    private bool _deletedMode = true;
    private int _recentCount = 5;

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
        AppVersion.Text = "Pairnets " + PairnetsInfo.ProductVersion + " · " + PairnetsInfo.Copyright;
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && !e.IsProgrammatic)
        {
            e.Cancel = true; // keep running in the menu bar / tray
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PopoverLayer.IsVisible)
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

    private void OnNav(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } nav || _navigating)
            return;
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
        PageTitle.Classes.Set("title", !overview);
        PageTitle.Classes.Set("muted", overview);
        PageTitle.FontSize = overview ? 15 : 26;
        PageTitle.FontWeight = FontWeight.SemiBold;
        AttentionCallout.Opacity = overview ? 1 : 0;
        AttentionCallout.IsHitTestVisible = overview;
        PageSubtitle.IsVisible = PageSubtitle.Text.Length > 0;
        OverviewPage.IsVisible = overview;
        ActivityPage.IsVisible = page == MainPage.Activity;
        HistoryPage.IsVisible = page == MainPage.History;
        DevicesPage.IsVisible = page == MainPage.Devices;
        AttentionPage.IsVisible = page == MainPage.Attention;
        SettingsPage.IsVisible = page == MainPage.Settings;
        AccountPage.IsVisible = page == MainPage.Account;
        foreach (var shown in new Control[] { OverviewPage, ActivityPage, HistoryPage, DevicesPage, AttentionPage, SettingsPage, AccountPage })
        {
            shown.Classes.Remove("enter");
            if (shown.IsVisible)
                shown.Classes.Add("enter");
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
        _status = s;
        ShowDevices(s);
        ShowRing(s);
        Headline.Text = s.Headline;
        var lines = OverviewLines.From(s, DateTimeOffset.Now);
        Lead.Text = lines.Lead;
        Lead.IsVisible = lines.Lead.Length > 0;
        DetailLines.ItemsSource = lines.Lines;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;
        WaitPanel.IsVisible = s.IsWaiting;
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        // Facts
        FolderText.Text = folder ?? "Not chosen yet";
        ToolTip.SetTip(FolderText, folder);
        LastSync.Text = s.LastSyncAt is { } at ? Format.Moment(at, DateTimeOffset.Now) : "Not yet";
        VersionText.Text = s.Server is null ? "–"
            : (s.Server.ServerVersion is { Length: > 0 } v ? "Version " + v : "Old version") + (s.ConnectionText is { } c ? " · " + c : string.Empty);
        VersionButton.IsVisible = s.Server is not null && s.ServerIsOlder;
        VersionButton.Content = s.ServerUpdateButtonText;
        VersionTile.Classes.Set("warn", s.ServerIsOlder);
        SpaceText.Text = s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " on the server" : "–";
        SpaceTile.Classes.Set("warn", s.ServerSpaceLow);

        // Fewer recent rows when the status above takes more room, so the facts stay in view.
        var recentCount = s.IsTransferring || s.IsWaiting ? 4 : 5;
        if (recentCount != _recentCount)
        {
            _recentCount = recentCount;
            LiveLists.Sync(_recent, ActivityGroups.Recent(_activityItems, _recentCount));
            UpdatePanels();
        }

        // In progress: files and folders of this sync; recent activity takes the whole width otherwise.
        TransferCard.IsVisible = s.IsTransferring;
        Grid.SetColumn(RecentPanel, s.IsTransferring ? 1 : 0);
        Grid.SetColumnSpan(RecentPanel, s.IsTransferring ? 1 : 2);
        var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
        if (s.IsTransferring)
            LiveLists.Sync(_active, TransferGroups.Build(s));
        else
            _active.Clear();
        Map.ShowSpeed(s.IsTransferring ? Format.Speed(s.BytesPerSecond) : null, uploading, s.IsTransferring && s.BytesPerSecond >= 1);
        Map.ShowOtherNote(s.IsWaiting ? s.WaitingFor!.Device + " uploading" : null);
        ShowAccount();
    }

    /// <summary>The big ring: how far, in which colour, with a number or an icon in the middle.</summary>
    private void ShowRing(StatusSnapshot s)
    {
        var ring = OverviewRing.From(s);
        _ringKind = ring.Kind;
        Visuals.Bind(Ring, ProgressRing.RingBrushProperty, ring.BrushKey);
        Ring.Value = ring.Spins ? 25 : ring.Percent;
        Ring.Classes.Set("spin", ring.Spins);
        RingText.Text = ring.CenterText ?? string.Empty;
        RingText.IsVisible = ring.CenterText is not null;
        RingCaption.Text = ring.Caption ?? string.Empty;
        RingCaption.IsVisible = ring.Caption is not null;
        RingIcon.IsVisible = ring.CenterText is null && ring.IconKey is not null && !ring.Spins;
        if (ring.IconKey is not null)
            RingIcon.Data = Visuals.Resource<Geometry>(ring.IconKey);
        Visuals.Bind(RingIcon, LineIcon.StrokeProperty, ring.BrushKey);
        RingHost.Classes.Set("pulse", ring.Breathes);

        // One small pop whenever the state changes (not while waiting: that breathes instead).
        var state = s.IsWaiting ? "waiting" : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
        {
            RingHost.Classes.Remove("pop");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RingHost.Classes.Add("pop"), Avalonia.Threading.DispatcherPriority.Background);
        }
        _shownState = state;
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
        AccountNestDetail.IsVisible = a.NestDetail.Length > 0;
        AccountDevice.Text = a.DeviceName;
        AccountDeviceDetail.Text = a.DeviceDetail;
        ToolTip.SetTip(AccountButton, a.Title);
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

    private void OnNotifyChanged(object? sender, RoutedEventArgs e)
    {
        if (_settingNotify || sender is not ToggleSwitch toggle)
            return;
        var kind = toggle == NotifyJoin ? NoticeKind.JoinRequest : toggle == NotifyAttention ? NoticeKind.Attention : NoticeKind.Update;
        _actions?.SetNotify(kind, toggle.IsChecked == true);
    }

    /// <summary>
    /// Shows or hides the app update (a button in the rail that opens a small notice). <paramref name="progress"/>
    /// 0–100 while downloading, null otherwise.
    /// </summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        UpdateRailButton.IsVisible = title is not null;
        if (title is null)
        {
            if (UpdateCard.IsVisible)
                ClosePopovers();
            return;
        }
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateButton.Content = button;
        UpdateButton.IsEnabled = !busy;
        UpdateLater.IsVisible = !busy;
        UpdateProgress.IsVisible = progress is not null;
        UpdateProgress.Value = progress ?? 0;
        ToolTip.SetTip(UpdateRailButton, title);
    }

    /// <summary>Opens the update notice next to the rail (also for screenshots).</summary>
    public void OpenUpdateNotice()
    {
        if (UpdateRailButton.IsVisible)
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
        AttentionBadge.IsVisible = items.Count > 0;
        AttentionCount.Text = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AttentionCallout.IsVisible = items.Count > 0;
        AttentionCalloutText.Text = items.Count == 1 ? "1 thing needs your attention" : $"{items.Count} things need your attention";
        ToolTip.SetTip(NavAttention, items.Count == 0 ? "Needs attention" : $"Needs attention ({items.Count})");
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        ActivityPanel.IsVisible = _activityItems.Count > 0;
        ActivityEmpty.IsVisible = _activityItems.Count == 0;
        RecentEmpty.IsVisible = _recent.Count == 0;
        AttentionListBorder.IsVisible = _attention.Count > 0;
        AttentionEmpty.IsVisible = _attention.Count == 0;
        DevicesPanel.IsVisible = _devices.Count > 0;
        DevicesEmpty.IsVisible = _devices.Count == 0;
    }

    // ------------------------------------------------------------------ popovers (drawn in the window, so screenshots show them)

    private void OpenPopover(Border popover, Button from)
    {
        var wasOpen = popover.IsVisible;
        ClosePopovers();
        if (wasOpen)
            return;
        PopoverLayer.IsVisible = true;
        popover.IsVisible = true;
        from.Classes.Add("open");
    }

    private void ClosePopovers()
    {
        PopoverLayer.IsVisible = false;
        UpdateCard.IsVisible = false;
        AccountMenu.IsVisible = false;
        UpdateRailButton.Classes.Remove("open");
        AccountButton.Classes.Remove("open");
    }

    private void OnToggleUpdate(object? sender, RoutedEventArgs e) => OpenPopover(UpdateCard, UpdateRailButton);

    private void OnToggleAccount(object? sender, RoutedEventArgs e) => OpenPopover(AccountMenu, AccountButton);

    /// <summary>A click outside the open menu closes it.</summary>
    private void OnPopoverLayerPressed(object? sender, PointerPressedEventArgs e) => ClosePopovers();

    /// <summary>Clicks inside a menu stay there.</summary>
    private void OnPopoverPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnAccountSettings(object? sender, RoutedEventArgs e) => Navigate(MainPage.Account);

    private void OnRenameInSettings(object? sender, RoutedEventArgs e)
    {
        if (_actions is not null)
            _actions.ShowSettings();
        else
            Navigate(MainPage.Settings);
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
        DetailStatePill.Classes.Set("bad", file.Deleted);
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
    internal RingKind RingKind => _ringKind;
    internal bool GlyphSpins => _ringKind == RingKind.Syncing;
    internal bool BadgePulses => RingHost.Classes.Contains("pulse");
    internal string ArrowMotion => Map.ArrowMotion;
    internal int ActiveRows => _active.Count;
    internal string MapOtherText => Map.OtherText;
    internal bool MapHereFlowing => Map.HereFlowing;
    internal int HistoryRows => _shownFiles.Count;
    internal int VersionRows => _versions.Count;
    internal string RestoreText => RestoreMessage.IsVisible ? RestoreMessageText.Text ?? string.Empty : string.Empty;
    internal bool CalloutVisible => AttentionCallout.IsVisible;
    internal int DeviceRows => _devices.Count;
    internal bool DevicesShown => DevicesPanel.IsVisible && DevicesPage.IsVisible;
    internal bool UpdateNoticeShown => UpdateCard.IsVisible;
    internal bool AccountMenuShown => AccountMenu.IsVisible;
    internal string AccountTitle => AccountPageTitle.Text ?? string.Empty;
    internal bool AttentionEmptyShown => AttentionEmpty.IsVisible;

    /// <summary>Switches to the Devices page (used by the headless UI test).</summary>
    internal void ShowDevicesTab() => Navigate(MainPage.Devices);

    internal void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    internal void SearchHistory(string text) => HistorySearch.Text = text;

    internal void ShowAllFiles() => FilesTab.IsChecked = true;

    internal Task RestoreFirstVersionAsync() => _versions.Count == 0 ? Task.CompletedTask : RestoreAsync(_versions[0]);

    /// <summary>Clicks a rail entry, as a person would.</summary>
    internal void ClickNav(MainPage page) => NavFor(page).IsChecked = true;

    /// <summary>Opens the first folder in "In progress" (screenshots).</summary>
    internal void ExpandFirstFolder()
    {
        foreach (var toggle in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(ActiveList).OfType<Avalonia.Controls.Primitives.ToggleButton>())
        {
            if (toggle.IsEffectivelyVisible)
            {
                toggle.IsChecked = true;
                return;
            }
        }
    }

    /// <summary>Presses "Later" in the update notice.</summary>
    internal void ClickUpdateLater() => UpdateLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal bool SettingsShown => SettingsPage.Content is not null;

    private void OnFix(object? sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object? sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object? sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnDownloadNow(object? sender, RoutedEventArgs e) => _actions?.DownloadNow();
    private void OnUpdateServer(object? sender, RoutedEventArgs e) => _actions?.UpdateServer();
    private void OnSeeAllActivity(object? sender, RoutedEventArgs e) => Navigate(MainPage.Activity);
    private void OnReviewAttention(object? sender, RoutedEventArgs e) => Navigate(MainPage.Attention);
    private void OnAddComputer(object? sender, RoutedEventArgs e) => _actions?.AddComputer();
    private void OnOpenNest(object? sender, RoutedEventArgs e) => _actions?.OpenNest();

    private void OnUpdateNow(object? sender, RoutedEventArgs e)
    {
        _actions?.UpdateNow();
    }

    private void OnUpdateLater(object? sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.DismissUpdate();
    }

    private void OnManageDevices(object? sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.ManageDevices();
    }

    private void OnSignOut(object? sender, RoutedEventArgs e)
    {
        ClosePopovers();
        _actions?.SignOut();
    }

    private void OnResetApp(object? sender, RoutedEventArgs e) => _actions?.ResetEverything();

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
