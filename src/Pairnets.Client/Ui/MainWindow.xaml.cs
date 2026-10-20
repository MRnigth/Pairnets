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

    /// <summary>The Files page on "All files": the nest's files and where each folder lives.</summary>
    Files,

    /// <summary>The Files page on "Deleted" (what was the History page).</summary>
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

    /// <summary>The nest's files for the Files page; null while not connected.</summary>
    INestFiles? Files => null;

    /// <summary>Opens a file in the program Windows uses for it (a file opened on the Files page).</summary>
    void OpenFile(string path)
    {
    }

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
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private string? _folder;
    private OverviewModel? _overview;
    private bool _navigating;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private SideUpdate _update = SideUpdate.None;
    private int _attentionCount;
    private bool _cleanup;
    private bool _fullShowing;

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        Overview.Act += OnOverviewAction;
        Overview.SeeAllActivity += () => Navigate(MainPage.Activity);
        Overview.MoreComputers += () => Navigate(MainPage.Devices);
        ActivityPage.Host = HistoryPage.Host = DevicesPage.Host = AccountPage.Host = NestFilesPage.Host = CleanupPage.Host = this;
        HistoryPage.DeletedCounted += count => DeletedTabCount.Text = Format.Count(count);
        AppIcon.Source = AppIcons.Large;
        DockIcon.Source = AppIcons.Large;
        RefreshSide();
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
    public NestFilesView FilesPane => NestFilesPage;
    public CleanupView CleanupPane => CleanupPage;

    /// <summary>True while the Files page shows "Clean up your nest".</summary>
    public bool CleanupShown => _cleanup && Page == MainPage.Files;

    /// <summary>The last status shown.</summary>
    public StatusSnapshot Status => _status;
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
            foreach (var tile in new[] { NavOverview, NavActivity, NavAttention, NavFiles, NavDevices, NavSettings, DockOverview, DockActivity, DockAttention, DockFiles, DockDevices, DockSettings })
                tile.IsChecked = false;
            return;
        }
        NavFor(page).IsChecked = true;
        DockFor(page).IsChecked = true;
    }

    private RadioButton NavFor(MainPage page) => page switch
    {
        MainPage.Activity => NavActivity,
        MainPage.Files or MainPage.History => NavFiles,
        MainPage.Devices => NavDevices,
        MainPage.Attention => NavAttention,
        MainPage.Settings => NavSettings,
        _ => NavOverview,
    };

    private RadioButton DockFor(MainPage page) => page switch
    {
        MainPage.Activity => DockActivity,
        MainPage.Files or MainPage.History => DockFiles,
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
            : nav == NavFiles || nav == DockFiles ? MainPage.Files
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
        var wasFiles = Page is MainPage.Files or MainPage.History && FilesPage.Visibility == Visibility.Visible;
        Page = page;
        _cleanup = false;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            MainPage.Activity => ("Activity", "What synced recently, newest first. Right-click a file for more."),
            MainPage.Files or MainPage.History => (FilesPageText.Title, FilesPageText.Subtitle),
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
                     (OverviewPage, MainPage.Overview), (ActivityPage, MainPage.Activity), (FilesPage, MainPage.Files),
                     (DevicesPage, MainPage.Devices), (AttentionPage, MainPage.Attention), (SettingsPage, MainPage.Settings),
                     (AccountPage, MainPage.Account),
                 })
        {
            var visible = shown == page || (shown == MainPage.Files && page == MainPage.History);
            if (shown == MainPage.Files && visible && wasFiles)
                continue; // another tab of the same page: it stays where it is
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
        if (page is MainPage.Files or MainPage.History)
        {
            ShowFilesPane();
            HistoryPage.Opened(); // also for the Deleted tab's count
            if (page == MainPage.Files)
                NestFilesPage.Refresh();
        }
        if (page == MainPage.Account)
            AccountPage.Show(_status, ThisDevice, null);
    }

    /// <summary>The Files page's two tabs and Clean up: which one shows.</summary>
    private void ShowFilesPane()
    {
        var was = _navigating;
        _navigating = true;
        try
        {
            AllFilesTab.IsChecked = Page == MainPage.Files;
            DeletedFilesTab.IsChecked = Page == MainPage.History;
        }
        finally
        {
            _navigating = was;
        }
        FilesHeader.Visibility = Show(!_cleanup);
        NestFilesPage.Visibility = Show(!_cleanup && Page == MainPage.Files);
        HistoryPage.Visibility = Show(!_cleanup && Page == MainPage.History);
        CleanupPage.Visibility = Show(_cleanup);
    }

    private void OnFilesTab(object sender, RoutedEventArgs e)
    {
        if (_navigating || NestFilesPage is null || sender is not RadioButton tab)
            return; // Checked also fires during InitializeComponent
        Navigate(tab == DeletedFilesTab ? MainPage.History : MainPage.Files);
    }

    /// <summary>Opens the Files page on <paramref name="folder"/>; with <paramref name="choosePlacement"/> its "where it lives" menu opens.</summary>
    public void ShowFilesAt(string folder, bool choosePlacement = false)
    {
        Navigate(MainPage.Files);
        NestFilesPage.ShowFolder(folder, choosePlacement);
    }

    /// <summary>Opens "Clean up your nest" on the Files page.</summary>
    public void ShowCleanup()
    {
        Navigate(MainPage.Files);
        _cleanup = true;
        ShowFilesPane();
        Motion.Enter(CleanupPage);
        CleanupPage.Refresh(_status.Server);
    }

    /// <summary>"Your nest is full", with "Help me clean up" (a change could not be made, or a file could not be saved back).</summary>
    public void ShowNestFull()
    {
        if (_fullShowing)
            return; // one at a time
        _fullShowing = true;
        try
        {
            var room = (FilesPageText.FreeLine(_status.Server) + " " + FilesPageText.FullSaving).Trim();
            if (Dialogs.Choose(IsVisible ? this : null, FilesPageText.FullTitle, FilesPageText.FullText, [room], FilesPageText.HelpMeCleanUp, "OK")
                == FilesPageText.HelpMeCleanUp)
                ShowCleanup();
        }
        finally
        {
            _fullShowing = false;
        }
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
        NestFilesPage.ShowStatus(s);
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
        UpdateDetail.Text = _update.ShowDetail ? detail : string.Empty;
        UpdateDetail.Visibility = Show(_update.ShowDetail);
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
