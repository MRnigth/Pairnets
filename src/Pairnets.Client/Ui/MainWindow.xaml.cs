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
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action,
    AttentionKind Kind = AttentionKind.Warning, string? Code = null, DateTimeOffset? At = null);

/// <summary>The pages in the main window's sidebar.</summary>
public enum MainPage
{
    Overview,
    Activity,
    History,
    Devices,
    Attention,
    Settings,

    /// <summary>Who is signed in, and the notification switches (the left side's account button opens it).</summary>
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

    /// <summary>"+ Add a computer…": how to add one (it signs in on the new computer).</summary>
    void AddComputer();

    /// <summary>Opens the nest's Devices page (rename, remove, approve).</summary>
    void ManageDevices();

    /// <summary>Shows a synced file in Explorer (or the folder, when the file is gone).</summary>
    void RevealFile(string syncPath);

    /// <summary>Where the History page reads from; null while not connected.</summary>
    IHistorySource? History { get; }

    /// <summary>Whether this kind of notification is switched on (the Account page's switches).</summary>
    bool Notifies(NoticeKind kind) => true;

    /// <summary>The Account page's switch for one kind of notification was changed: remember it.</summary>
    void SetNotifies(NoticeKind kind, bool on)
    {
    }

    /// <summary>"Sign out" on the Account page (the controller asks first).</summary>
    void SignOut()
    {
    }

    /// <summary>"Manage your account on sync.pairnets.app": opens the website.</summary>
    void OpenAccountSite()
    {
    }
}

/// <summary>
/// The main window: a sidebar with Overview (status, the two computers, facts, recent activity),
/// Activity, History, Needs attention and Settings. It only displays what <see cref="ClientSession"/>
/// reports and forwards clicks. Same design as the Mac/Linux app.
/// </summary>
public partial class MainWindow : Window, IPageHost
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<ActivityItem> _recent = [];
    private readonly ObservableCollection<ActiveFileView> _active = [];
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private string? _shownState;
    private string? _tintKey;
    private bool _navigating;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private SideUpdate _update = SideUpdate.None;
    private int _attentionCount;

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        RecentList.ItemsSource = _recent;
        ActiveList.ItemsSource = _active;
        ActivityPage.Host = HistoryPage.Host = DevicesPage.Host = AccountPage.Host = this;
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

    /// <summary>What the pages ask of the app (the pages are given this window as their host).</summary>
    public IMainActions? Actions => _actions;

    /// <summary>The pages (for the checks and the screenshot tool).</summary>
    public ActivityPageView ActivityPane => ActivityPage;
    public HistoryPageView HistoryPane => HistoryPage;
    public DevicesPageView DevicesPane => DevicesPage;
    public NeedsPageView NeedsPane => AttentionPage;
    public AccountPageView AccountPane => AccountPage;

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
            CheckTiles(page);
        }
        finally
        {
            _navigating = false;
        }
        ShowPage(page);
    }

    /// <summary>Chooses the tile and the dock button of a page; on the Account page none is chosen.</summary>
    private void CheckTiles(MainPage page)
    {
        if (page == MainPage.Account)
        {
            foreach (var tile in new[] { NavOverview, NavActivity, NavAttention, NavHistory, NavDevices, NavSettings, DockOverview, DockActivity, DockAttention, DockHistory, DockDevices, DockSettings })
                tile.IsChecked = false;
            return;
        }
        NavFor(page).IsChecked = true;
        DockFor(page).IsChecked = true;
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
            CheckTiles(page);
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
            MainPage.History => ("History", "Get back deleted files and older versions. Your nest keeps them for 30 days."),
            MainPage.Devices => ("Devices", "Every computer on your nest, and the nest itself."),
            MainPage.Attention => ("Needs you", "Things only you can decide."),
            MainPage.Settings => ("Settings", "Your nest, the folder, and how Pairnets behaves on this computer."),
            MainPage.Account => ("Account", "Who is signed in on this computer, and when Pairnets should tell you things."),
            _ => ("Overview", string.Empty),
        };
        PageSubtitle.Visibility = Show(PageSubtitle.Text.Length > 0);
        // The pages draw their own header; the shared one belongs to the Overview.
        PageHeader.Visibility = Show(page == MainPage.Overview);
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
            ActivityPage.Show(_activityItems);
        if (page == MainPage.Devices)
        {
            DevicesPage.Show(_status, ThisDevice, null);
            _actions?.RefreshDevices();
        }
        if (page == MainPage.History)
            HistoryPage.Opened();
        if (page == MainPage.Account)
            AccountPage.Show(_status, ThisDevice, null);
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
        RefreshSide();
        DevicesPage.Show(s, ThisDevice, null);
        if (Page == MainPage.Account)
            AccountPage.Show(s, ThisDevice, null);
        if (folder is not null)
            ActivityPage.Folder = folder;
        var brush = Badge.Show(s, StatusBadge, StatusGlyph, StatusGlyph2, 23, animate: true);
        Tint(brush);
        Animate(s);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText;
        Detail.Visibility = Show(s.DetailText.Length > 0);
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.Visibility = Show(s.FixLabel is not null);
        FixButton.Content = s.FixLabel;
        Map.ShowSpeed = ShowMapSpeed;
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        // Facts
        FolderText.Text = folder ?? "Not chosen yet";
        FolderText.ToolTip = folder;
        LastSync.Text = s.LastSyncAt is { } at ? Format.Moment(at, DateTimeOffset.Now) : "Not yet";
        ConnectionText.Text = s.ConnectionText ?? "Connecting…";
        VersionText.Text = s.Server is null ? "–" : s.Server.ServerVersion is { Length: > 0 } v ? "Version " + v : "Old version";
        VersionButton.Visibility = Show(s.Server is not null);
        VersionButton.Content = s.ServerUpdateButtonText;
        VersionTile.SetResourceReference(Border.BackgroundProperty, s.ServerIsOlder ? "T.WarnPill" : "T.Card");
        VersionText.SetResourceReference(TextBlock.ForegroundProperty, s.ServerIsOlder ? "T.WarnText" : "T.Text");
        SpaceText.Text = s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) : "–";
        SpaceTile.SetResourceReference(Border.BackgroundProperty, s.ServerSpaceLow ? "T.WarnPill" : "T.Card");
        SpaceText.SetResourceReference(TextBlock.ForegroundProperty, s.ServerSpaceLow ? "T.WarnText" : "T.Text");

        TransferCard.Visibility = Show(s.IsTransferring);
        if (!s.IsTransferring)
            Motion.Travel(TransferArrow, null);
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            var stall = s.StallText(DateTimeOffset.UtcNow);
            TransferSpeed.Text = stall.Length > 0 ? stall : s.SpeedText;
            TransferSpeed.SetResourceReference(TextBlock.ForegroundProperty, stall.Length > 0 ? "T.WarnText" : "T.Muted");
            LimitPill.Visibility = Show(s.LimitText is not null);
            LimitText.Text = s.LimitText ?? string.Empty;
            Motion.Glide(TransferProgress, s.OverallPercent ?? 0);
            TransferOverall.Text = s.OverallText;
            var several = s.Active.Count > 1 || s.FilesTotal > 1;
            ActiveDivider.Visibility = Show(several && s.Active.Count > 0);
            ActiveList.Visibility = Show(several);
            LiveLists.Sync(_active, s.Active);
            var uploading = s.GoingUp || !s.GoingDown;
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.SetResourceReference(LineIcon.StrokeProperty, uploading ? "S.Green" : "S.Blue");
            var stalled = s.Motion == SyncMotion.Stalled;
            TransferArrow.Opacity = stalled ? 0.5 : 1;
            Motion.Travel(TransferArrow, stalled ? null : uploading);
        }

        // Waiting for the other computer's big batch.
        WaitPanel.Visibility = Show(s.IsWaiting);
        if (s.IsWaiting)
        {
            Motion.Glide(WaitProgress, s.WaitingPercent ?? 0);
            WaitText.Text = s.WaitingProgressText;
        }
    }

    /// <summary>A soft wash of the status colour across the top of the status card.</summary>
    private void Tint(string brushKey)
    {
        if (_tintKey == brushKey)
            return;
        _tintKey = brushKey;
        HeroTint.Background = Visuals.Wash(brushKey, 0x24, 0.75);
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
        LiveLists.Sync(_recent, items.Take(5).ToList());
        if (Page == MainPage.Activity)
            ActivityPage.Show(items);
        UpdatePanels();
    }

    /// <summary>
    /// Motion for the status badge (its glyph moves in <see cref="Badge"/>): it breathes while waiting for the
    /// other computer, and pops once whenever the state changes, also between uploading, downloading and stalled.
    /// </summary>
    private void Animate(StatusSnapshot s)
    {
        Motion.Pulse(StatusBadgeHost, s.IsWaiting);
        var state = s.IsWaiting ? "waiting" : s.Status == Pairnets.Core.Sync.RunnerStatus.Syncing ? s.Motion.ToString() : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
            Motion.Pop(StatusBadgeHost);
        _shownState = state;
    }

    private void StopMotion()
    {
        Motion.Spin(StatusGlyph, false);
        Motion.Travel(StatusGlyph, null);
        Motion.Travel(StatusGlyph2, null);
        Motion.Pulse(StatusBadgeHost, false);
        Motion.Travel(TransferArrow, null);
        Motion.Bob(UpdateIcon, false);
        _shownState = null;
    }

    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement row)
            Motion.Enter(row);
    }

    private void OnAddComputer(object sender, RoutedEventArgs e) => _actions?.AddComputer();

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        AttentionPage.Show(items);
        _attentionCount = items.Count;
        RefreshSide();
        AttentionCallout.Visibility = Show(items.Count > 0);
        AttentionCalloutText.Text = items.Count == 1 ? "1 thing needs your attention" : $"{items.Count} things need your attention";
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (RecentEmpty is null)
            return; // Checked fires during InitializeComponent
        RecentEmpty.Visibility = Show(_recent.Count == 0);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ history (the page itself is HistoryPageView)

    /// <summary>Reads the nest's file list (deleted files included) for the History page.</summary>
    public Task LoadHistoryAsync() => HistoryPage.LoadAsync();

    /// <summary>Opens History on one file (from the activity list): its versions, or the deleted table filtered to it.</summary>
    public void ShowVersionsFor(string path)
    {
        HistoryPage.ChooseAfterLoad(path);
        if (Page == MainPage.History)
            _ = HistoryPage.LoadAsync();
        else
        {
            HistoryPage.Forget(); // read fresh, so the file is there
            Navigate(MainPage.History);
        }
    }

    /// <summary>Restores one version of a file (History's version list).</summary>
    public Task RestoreAsync(VersionRow row) => HistoryPage.RestoreAsync(row);

    /// <summary>Selects a row in the History list (the screenshot tool).</summary>
    public void SelectHistoryRow(int index) => HistoryPage.SelectHistoryRow(index);

    /// <summary>What the overview's picture says about the other computer (checks).</summary>
    public string MapOtherText => Map.OtherText;

    private void OnFix(object sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnUpdateNow(object sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object sender, RoutedEventArgs e) => _actions?.DismissUpdate();
    private void OnDownloadNow(object sender, RoutedEventArgs e) => _actions?.DownloadNow();
    private void OnUpdateServer(object sender, RoutedEventArgs e) => _actions?.UpdateServer();
    private void OnSeeAllActivity(object sender, RoutedEventArgs e) => Navigate(MainPage.Activity);
    private void OnReviewAttention(object sender, RoutedEventArgs e) => Navigate(MainPage.Attention);

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
}
