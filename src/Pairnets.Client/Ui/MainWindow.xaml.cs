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

    /// <summary>Saves the icon picked in the chooser on the nest (one of <see cref="Pairnets.Core.DeviceIcons.All"/>).</summary>
    Task<bool> SetDeviceIcon(string id, string icon);

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

    /// <summary>"Sign in again" on the Account page (the sign-in window opens).</summary>
    void SignInAgain()
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
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private string? _folder;
    private OverviewModel? _overview;
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
        Overview.Act += OnOverviewAction;
        Overview.SeeAllActivity += () => Navigate(MainPage.Activity);
        Overview.MoreComputers += () => Navigate(MainPage.Devices);
        ActivityPage.Host = HistoryPage.Host = DevicesPage.Host = AccountPage.Host = this;
        AppIcon.Source = AppIcons.Large;
        DockIcon.Source = AppIcons.Large;
        RefreshSide();
        HintShortcuts();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopMotion(); // nothing moves (or costs CPU) while Pairnets sits in the tray
        };
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
        DevicesPage.Show(s, ThisDevice, null);
        if (Page == MainPage.Account)
            AccountPage.Show(s, ThisDevice, null);
        if (folder is not null)
            ActivityPage.Folder = folder;
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
        DrawSyncNow(_overview.SyncNow);
    }

    /// <summary>
    /// The header's "Sync now": the blue main button only when syncing is the useful thing to do; plain when the card has its
    /// own action (so that one is the only blue button), and resting while a sync runs.
    /// </summary>
    private void DrawSyncNow(OverviewSyncNow look)
    {
        if (_syncNowPrimary != look.IsPrimary)
        {
            _syncNowPrimary = look.IsPrimary;
            SyncNowButton.Style = (Style)FindResource(look.IsPrimary ? "AccentButton" : "SubtleButton");
        }
        // Set on every redraw (every few seconds), so a theme change reaches the plain button's icon too.
        SyncNowIcon.Stroke = look.IsPrimary ? Brushes.White : (Brush)FindResource("T.Text");
        SyncNowButton.IsEnabled = look.IsEnabled;
        SyncNowButton.ToolTip = look.ToolTip(mac: false);
    }

    private bool _syncNowPrimary = true;

    /// <summary>Whether the header's "Sync now" is the blue main button right now (the checks).</summary>
    public bool SyncNowIsPrimary => _syncNowPrimary;

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
            ActivityPage.Show(items);
    }


    private void StopMotion()
    {
        Overview.StopMotion();
        Motion.Bob(UpdateIcon, false);
    }

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        AttentionPage.Show(items);
        _attentionCount = items.Count;
        RefreshSide();
        RefreshOverview();
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
    public string MapOtherText => Overview.OtherText;

    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnAddComputer(object sender, RoutedEventArgs e) => _actions?.AddComputer();

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
}
