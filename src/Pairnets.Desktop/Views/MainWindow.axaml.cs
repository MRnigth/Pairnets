using Pairnets.Core.Api;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>An item in the "Needs attention" list with one action button (and sometimes a second, quieter one).</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action,
    AttentionKind Kind = AttentionKind.Warning, string? Code = null, DateTimeOffset? At = null,
    string? SecondLabel = null, Action? Second = null);

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

    /// <summary>Saves the icon picked in the chooser on the nest (one of <see cref="Pairnets.Core.DeviceIcons.All"/>).</summary>
    Task<bool> SetDeviceIcon(string id, string icon);

    /// <summary>"+ Add a computer…": how to add one (it signs in on the new computer).</summary>
    void AddComputer();

    /// <summary>Opens the nest's Devices page (rename, remove, approve).</summary>
    void ManageDevices();

    /// <summary>Shows a synced file in the file manager (or the folder, when the file is gone).</summary>
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

public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<object> _activityRows = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<ServerFile> _shownFiles = [];
    private readonly ObservableCollection<VersionRow> _versions = [];
    private readonly ObservableCollection<DeviceCard> _devices = [];
    private IReadOnlyList<ActivityItem> _activityItems = [];
    private IReadOnlyList<ServerFile>? _serverFiles;
    private DateTimeOffset _serverFilesAt;
    private CancellationTokenSource? _versionsCts;
    private string? _selectAfterLoad;
    private string? _folder;
    private OverviewModel? _overview;
    private bool _navigating;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private SideUpdate _update = SideUpdate.None;
    private int _attentionCount;
    private bool _deletedMode = true;
    private string? _filteredSearch;

    private static readonly TimeSpan HistoryFreshFor = TimeSpan.FromMinutes(1);

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(IMainActions? actions)
    {
        InitializeComponent();
        _actions = actions;
        Overview.Act += OnOverviewAction;
        Overview.SeeAllActivity += () => Navigate(MainPage.Activity);
        Overview.MoreComputers += () => Navigate(MainPage.Devices);
        ActivityList.ItemsSource = _activityRows;
        AttentionList.ItemsSource = _attention;
        FileList.ItemsSource = _shownFiles;
        VersionList.ItemsSource = _versions;
        DevicesList.ItemsSource = _devices;
        // As many computer cards side by side as fit (3 cut them off in a 980 px window). Each card's 6 px margins are
        // part of its cell, as on Windows.
        NestChips.SizeChanged += (_, e) => NestChips.Columns = PageLayout.ChipColumns(e.NewSize.Width);
        DevicesList.SizeChanged += (_, e) =>
        {
            if (DevicesList.ItemsPanelRoot is UniformGrid grid)
                grid.Columns = PageLayout.CardColumns(e.NewSize.Width + 12);
        };
        DeletedList.ItemsSource = _shownFiles;
        AccountPage.Attach(actions);
        WirePages();
        WireShortcuts();
        AttentionSubtitle.Text = AttentionSubtitleText(0);
        RefreshSide();
        UpdatePanels();
    }

    /// <summary>The longer words behind the update card's title (its tooltip), for the tests.</summary>
    internal string UpdateDetailText => ToolTip.GetTip(UpdateTitle) as string ?? string.Empty;

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainPage Page { get; private set; } = MainPage.Overview;

    /// <summary>This computer's name, marked in the Devices list and shown first in the overview's picture.</summary>
    public string? ThisDevice { get; set; }

    /// <summary>Show the MB/s on the overview picture's lines (Settings: "Show speed on the map").</summary>
    public bool ShowMapSpeed { get; set; } = true;

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
            CheckTiles(page);
        }
        finally
        {
            _navigating = false;
        }
        ShowPage(page);
    }

    /// <summary>Checks the left side's tile and dock button of a page; the Account page has none, so none is checked.</summary>
    private void CheckTiles(MainPage page)
    {
        if (page == MainPage.Account)
        {
            foreach (var tile in new[] { NavOverview, NavActivity, NavAttention, NavHistory, NavDevices, NavSettings,
                         DockOverview, DockActivity, DockAttention, DockHistory, DockDevices, DockSettings })
                tile.IsChecked = false;
            return;
        }
        NavFor(page).IsChecked = true;
        DockFor(page).IsChecked = true;
    }

    private RadioButton DockFor(MainPage page) => page switch
    {
        MainPage.Activity => DockActivity,
        MainPage.History => DockHistory,
        MainPage.Devices => DockDevices,
        MainPage.Attention => DockAttention,
        MainPage.Settings => DockSettings,
        _ => DockOverview,
    };

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
            MainPage.Devices => ("Computers", "Every computer on your nest, and the nest itself."),
            MainPage.Attention => ("Needs you", "Things only you can decide."),
            MainPage.Settings => ("Settings", "Your nest, the folder, and how Pairnets behaves on this computer."),
            MainPage.Account => ("Account", "Who is signed in on this computer, and when Pairnets should tell you things."),
            _ => ("Overview", string.Empty),
        };
        PageSubtitle.IsVisible = PageSubtitle.Text.Length > 0;
        HeaderRow.IsVisible = page == MainPage.Overview; // every other page draws its own header
        OverviewPage.IsVisible = page == MainPage.Overview;
        ActivityPage.IsVisible = page == MainPage.Activity;
        HistoryPage.IsVisible = page == MainPage.History;
        DevicesPage.IsVisible = page == MainPage.Devices;
        AttentionPage.IsVisible = page == MainPage.Attention;
        SettingsPage.IsVisible = page == MainPage.Settings;
        AccountPage.IsVisible = page == MainPage.Account;
        foreach (var shown in new Control[] { OverviewPage, ActivityPage, HistoryPage, DevicesPage, AttentionPage, SettingsPage, AccountPage })
        {
            shown.Classes.Remove("enter");
            Motion.Set(shown, "enter", shown.IsVisible);
        }
        if (page == MainPage.Activity)
            RebuildActivity();
        if (page == MainPage.Account)
            AccountPage.Show(_status, ThisDevice);
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
        if (folder is not null)
            _folder = folder;
        _status = s;
        RefreshSide();
        ShowDevices(s);
        if (Page == MainPage.Account)
            AccountPage.Show(s, ThisDevice);
        ActivityEmptyText.Text = EmptyFolderText();
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        RefreshMacMenu();
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
        SyncNowButton.Classes.Set("accent", look.IsPrimary);
        SyncNowButton.Classes.Set("subtle", !look.IsPrimary);
        SyncNowIcon.Classes.Set("white", look.IsPrimary);
        SyncNowButton.IsEnabled = look.IsEnabled;
        ToolTip.SetTip(SyncNowButton, look.ToolTip(OperatingSystem.IsMacOS()));
    }

    /// <summary>Whether the header's "Sync now" is the blue main button right now (the checks).</summary>
    internal bool SyncNowIsPrimary => SyncNowButton.Classes.Contains("accent");

    internal bool SyncNowEnabled => SyncNowButton.IsEnabled;

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
                OnAccount(this, new Avalonia.Interactivity.RoutedEventArgs());
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

    /// <summary>The Devices page: the nest, and the server's list with what each computer moves right now.</summary>
    private void ShowDevices(StatusSnapshot s)
    {
        var nest = NestCard.From(s, null);
        NestHeading.Text = nest.Title;
        NestAddress.Text = nest.Address;
        NestVersion.Text = nest.Version;
        NestVersionNote.Text = nest.VersionNote;
        NestVersionNote.Foreground = Visuals.Resource<IBrush>(nest.VersionOlder ? "T.WarnText" : "T.OkText");
        NestSpace.Text = nest.FreeSpace;
        NestKeeps.Text = nest.Keeps;
        NestOnlineText.Text = nest.OnlineText;
        NestOnlinePill.Classes.Set("pillok", nest.Online);
        if (nest.Online) NestOnlinePill.ClearValue(Border.BackgroundProperty); else NestOnlinePill.Background = Visuals.Resource<IBrush>("T.BadPill");
        NestOnlineDot.Fill = Visuals.Resource<IBrush>(nest.Online ? "T.Ok" : "T.Bad");
        NestOnlineText.Foreground = Visuals.Resource<IBrush>(nest.Online ? "T.OkText" : "T.BadText");

        if (s.DevicesUnsupported)
            DevicesEmptyText.Text = "Update your nest to see the computers that use it.";
        if (s.Devices is not null)
        {
            var cards = DeviceCard.From(s, ThisDevice, DateTimeOffset.UtcNow);
            var count = _devices.Count;
            SyncCards(cards);
            DevicesSummaryText.Text = DevicesSummary.Line(cards);
            var join = DevicesSummary.JoinText(s);
            JoinChip.IsVisible = join is not null;
            JoinChipText.Text = join ?? string.Empty;
            if (s.Devices.Count == 0 && !s.DevicesUnsupported)
                DevicesEmptyText.Text = "No computers have used this nest yet.";
            if (count != _devices.Count)
                UpdatePanels();
        }
    }

    /// <summary>Brings the cards to the new list: the same computers stay in place (live speeds change every second).</summary>
    private void SyncCards(IReadOnlyList<DeviceCard> cards)
    {
        var same = _devices.Count == cards.Count;
        for (var i = 0; same && i < cards.Count; i++)
            same = string.Equals(_devices[i].Name, cards[i].Name, StringComparison.Ordinal) && _devices[i].IsThis == cards[i].IsThis;
        if (!same)
        {
            _devices.Clear();
            foreach (var card in cards)
                _devices.Add(card);
            return;
        }
        for (var i = 0; i < cards.Count; i++)
        {
            if (_devices[i] != cards[i])
                _devices[i] = cards[i];
        }
    }

    /// <summary>
    /// Shows or hides the app update card. <paramref name="progress"/> 0–100 while downloading, null otherwise.
    /// </summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        _update = SideUpdate.From(title, detail, button, progress, busy);
        UpdateCard.IsVisible = _update.Visible;
        Motion.Set(UpdateIcon, "bob", _update.Visible && !busy);
        if (!_update.Visible)
            return;
        UpdateTitle.Text = _update.Title;
        ToolTip.SetTip(UpdateTitle, detail.Length > 0 ? detail : null);
        UpdateReason.Text = _update.ShowsReason ? detail : string.Empty;
        UpdateReason.IsVisible = _update.ShowsReason;
        UpdateButton.Content = button;
        UpdateButton.IsVisible = _update.HasButton;
        UpdateLater.IsVisible = !busy;
        UpdatePercent.IsVisible = busy;
        UpdatePercent.Text = _update.PercentText;
        UpdateProgress.IsVisible = _update.ShowBar;
        UpdateProgress.Value = progress ?? 0;
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activityItems = items;
        RefreshSide();
        RefreshOverview();
        if (Page == MainPage.Activity)
            RebuildActivity();
        UpdatePanels();
    }

    private void OnAddComputer(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _actions?.AddComputer();

    private void OnManageDevices(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _actions?.ManageDevices();

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        _attentionCount = items.Count;
        RefreshSide();
        RefreshOverview();
        AttentionSubtitle.Text = AttentionSubtitleText(items.Count);
        UpdatePanels();
    }

    private static string AttentionSubtitleText(int count) => count switch
    {
        0 => "Nothing needs a decision right now. Everything keeps syncing.",
        1 => "1 thing only you can decide. Everything else keeps syncing meanwhile.",
        _ => $"{count} things only you can decide. Everything else keeps syncing meanwhile.",
    };

    private void UpdatePanels()
    {
        ActivityEmpty.IsVisible = _activityItems.Count == 0;
        ActivityTools.IsVisible = ActivityListCard.IsVisible = _activityItems.Count > 0;
        AttentionList.IsVisible = _attention.Count > 0;
        AttentionEmpty.IsVisible = _attention.Count == 0;
        DevicesPanel.IsVisible = _devices.Count > 0;
        DevicesEmpty.IsVisible = _devices.Count == 0;
    }

    // ------------------------------------------------------------------ activity

    private ActivityFilter _activityFilter = ActivityFilter.All;
    private string _activitySearchText = string.Empty;
    private readonly Dictionary<ActivityFilter, (Button Button, TextBlock Count)> _pills = [];

    /// <summary>Builds the pieces of the pages that are not in the XAML (the filter pills) and hooks their events.</summary>
    private void WirePages()
    {
        foreach (var filter in ActivityView.Filters)
        {
            var count = new TextBlock { Classes = { "n" }, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var button = new Button
            {
                Classes = { "fpill" },
                Tag = filter,
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    Children = { new TextBlock { Text = ActivityView.Label(filter), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, count },
                },
            };
            button.Click += OnFilterClick;
            _pills[filter] = (button, count);
            ActivityFilters.Children.Add(button);
        }
        ActivitySearch.TextChanged += OnActivitySearch;
        ActivityTools.SizeChanged += (_, _) => FitActivityTools();
        ShowInFolderButton.Click += OnShowInFolder;
        AddHandler(DeviceCardView.MoreRequestedEvent, OnDeviceMore);
        DrawPills();
    }

    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ActivityFilter filter })
            SetActivityFilter(filter);
    }

    private void OnActivitySearch(object? sender, TextChangedEventArgs e)
    {
        var text = ActivitySearch.Text ?? string.Empty;
        if (text == _activitySearchText)
            return;
        _activitySearchText = text;
        RebuildActivity();
    }

    internal void SetActivityFilter(ActivityFilter filter)
    {
        _activityFilter = filter;
        RebuildActivity();
    }

    private void DrawPills()
    {
        var counts = ActivityView.Counts(_activityItems, _activitySearchText);
        foreach (var (filter, (button, count)) in _pills)
        {
            button.Classes.Set("sel", filter == _activityFilter);
            count.Text = Format.Count(counts.Of(filter));
        }
        FitActivityTools();
    }

    /// <summary>
    /// The search box beside the filter buttons when both fit, else on its own line under them (in a 980 px window it
    /// pushed the buttons onto two lines).
    /// </summary>
    private void FitActivityTools()
    {
        var width = ActivityTools.Bounds.Width;
        if (width <= 0)
            return;
        var filters = 0.0;
        foreach (var pill in ActivityFilters.Children)
        {
            pill.Measure(Size.Infinity);
            filters += pill.DesiredSize.Width;
        }
        var below = PageLayout.SearchGoesBelow(filters, width);
        Grid.SetRow(ActivitySearch, below ? 1 : 0);
        Grid.SetColumn(ActivitySearch, below ? 0 : 1);
        Grid.SetColumnSpan(ActivitySearch, below ? 2 : 1);
        ActivitySearch.Width = below ? double.NaN : PageLayout.SearchWidth;
        ActivitySearch.Margin = below ? new Thickness(0, 4, 0, 0) : new Thickness(PageLayout.SearchGap, 0, 0, 0);
    }

    /// <summary>Draws the Activity list for the chosen filter and search (the same Core rows as the Windows app).</summary>
    private void RebuildActivity()
    {
        DrawPills();
        var rows = ActivityView.Rows(_activityItems, _activityFilter, _activitySearchText, DateTimeOffset.Now);
        LiveLists.Sync(_activityRows, rows);
        var none = rows.Count == 0 && _activityItems.Count > 0;
        ActivityNoMatch.IsVisible = none;
        ActivityNoMatch.Text = ActivityView.EmptyText(_activityItems, _activityFilter, _activitySearchText);
        ActivityPanel.IsVisible = rows.Count > 0;
    }

    private string EmptyFolderText() =>
        $"Put a file in {(string.IsNullOrWhiteSpace(_folder) ? "your synced folder" : _folder)} and it shows up here as soon as it is safe on your nest.";

    private void OnReviewConflict(object? sender, RoutedEventArgs e) => Navigate(MainPage.Attention);

    private void OnReviewJoin(object? sender, RoutedEventArgs e) => Navigate(MainPage.Attention);

    private void OnDeviceMore(object? sender, RoutedEventArgs e) => _actions?.ManageDevices();

    // ------------------------------------------------------------------ history

    /// <summary>Reads the nest's file list (deleted files included) for the History page.</summary>
    internal async Task LoadHistoryAsync()
    {
        var source = _actions?.History;
        if (source is null)
        {
            ShowHistoryMessage("Not connected to your nest. History appears here once Pairnets is connected.", retry: false);
            return;
        }
        ShowHistoryMessage("Loading the files on your nest…", retry: false);
        try
        {
            _serverFiles = await source.GetServerFilesAsync(CancellationToken.None);
            _serverFilesAt = DateTimeOffset.Now;
            DeletedCount.Text = HistoryQuery.Filter(_serverFiles, true, null, DateTimeOffset.UtcNow).Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            ShowHistoryMessage("Could not read the files on your nest. " + FriendlyErrors.Describe(ex), retry: true);
        }
    }

    /// <summary>Opens History on one file (from the activity list): a current file in "All files" with its versions, a deleted one in "Deleted files".</summary>
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
        DeletedPane.IsVisible = false;
        FilesPane.IsVisible = false;
        HistoryMessageCard.IsVisible = true;
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
        _filteredSearch = search ?? string.Empty;
        HistorySearch.Watermark = deleted ? "Search deleted files by name or folder" : "Search all files by name or folder";
        var (rows, total) = HistoryQuery.Filter(_serverFiles, deleted, search, DateTimeOffset.UtcNow);
        var selected = (FileList.SelectedItem as ServerFile)?.Path;
        _shownFiles.Clear();
        foreach (var row in rows)
            _shownFiles.Add(row);
        HistorySummary.Text = HistoryQuery.Summary(rows.Count, total, deleted, !string.IsNullOrWhiteSpace(search));
        DeletedPane.IsVisible = deleted && rows.Count > 0;
        FilesPane.IsVisible = !deleted && rows.Count > 0;
        HistoryMessageCard.IsVisible = rows.Count == 0;
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
        DeletedMessage.IsVisible = false;
        FilterHistory();
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Avalonia reports a search the code typed in later, through the dispatcher, when _navigating is off again.
        // The list already shows that search; filtering again would reselect the file and hide what the page just
        // said (the "Restored." after Restore).
        if (!_navigating && (HistorySearch.Text ?? string.Empty) != _filteredSearch)
        {
            DeletedMessage.IsVisible = false;
            FilterHistory();
        }
    }

    private void OnHistoryReload(object? sender, RoutedEventArgs e)
    {
        DeletedMessage.IsVisible = false;
        _ = LoadHistoryAsync();
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e) => ShowDetail(FileList.SelectedItem as ServerFile);

    private void OnShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is ServerFile { Deleted: false } file)
            _actions?.RevealFile(file.Path);
    }

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
        DetailFolder.Text = file.InFolderText;
        ShowInFolderButton.IsVisible = !file.Deleted;
        CurrentVersion.IsVisible = !file.Deleted;
        var when = file.WhenText;
        CurrentWhen.Text = when.Length > 0 ? char.ToUpperInvariant(when[0]) + when[1..] : when;
        CurrentSize.Text = file.SizeText;
        VersionCount.Text = string.Empty;
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
            VersionCount.Text = VersionRow.CountText(_versions.Count);
            VersionsMessage.IsVisible = _versions.Count == 0;
            VersionsMessage.Text = file.Deleted
                ? "Your nest no longer has a copy: it was deleted more than 30 days ago."
                : "No older versions: this file has not been changed or replaced in the last 30 days.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                VersionsMessage.Text = "Could not read the versions. " + FriendlyErrors.Describe(ex);
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

    private async void OnRestoreFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerFile file } button)
            return;
        button.IsEnabled = false;
        try
        {
            await RestoreDeletedAsync(file);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>"Restore" on a row of the Deleted files table: brings back the file's newest stored version.</summary>
    internal async Task RestoreDeletedAsync(ServerFile file)
    {
        if (_actions?.History is not { } source)
            return;
        string? error;
        try
        {
            var versions = await source.GetVersionsAsync(file.Path, CancellationToken.None);
            error = versions.Count == 0
                ? "Your nest no longer has a copy of this file."
                : await source.RestoreVersionAsync(file.Path, versions[0], CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = "Could not restore it: " + ex.Message;
        }
        if (error is null)
            await LoadHistoryAsync(); // the file is a current one again: it leaves this table
        DeletedMessage.IsVisible = true;
        DeletedMessage.Classes.Set("ok", error is null);
        DeletedMessage.Classes.Set("bad", error is not null);
        DeletedMessageText.Text = error ?? $"Restored. {file.Name} is back on your computers in a few seconds.";
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Overview.TitleText;
    internal bool FixVisible => _overview?.HasFix == true;
    internal int ActivityCount => _activityItems.Count;
    internal int ActivityRowCount => _activityRows.Count;
    internal bool TransferVisible => Overview.MovingVisible;
    /// <summary>"rise", "fall", "spin-slow" or "none": how the badge's glyph moves.</summary>
    internal string GlyphMotion => _overview?.Flow switch { OverviewFlow.Up or OverviewFlow.Both => "rise", OverviewFlow.Down => "fall", OverviewFlow.Checking => "spin-slow", _ => "none" };

    internal bool SecondGlyphFalls => _overview?.Flow == OverviewFlow.Both;
    internal bool BadgePulses => Overview.Pulsing;
    internal string ArrowMotion => _overview?.Flow switch { OverviewFlow.Up or OverviewFlow.Both => "rise", OverviewFlow.Down => "fall", _ => "none" };
    internal int ActiveRows => Overview.MovingRows;
    internal string MapOtherText => Overview.OtherText;
    internal bool MapHereFlowing => Overview.ThisFlowing;
    internal int HistoryRows => _shownFiles.Count;
    internal int VersionRows => _versions.Count;
    internal string RestoreText => DeletedMessage.IsVisible ? DeletedMessageText.Text ?? string.Empty
        : RestoreMessage.IsVisible ? RestoreMessageText.Text ?? string.Empty : string.Empty;
    internal bool CalloutVisible => Overview.NeedsVisible;
    internal int DeviceRows => _devices.Count;
    internal bool DevicesShown => DevicesPanel.IsVisible && DevicesPage.IsVisible;
    internal bool JoinChipShown => JoinChip.IsVisible;
    internal string JoinChipValue => JoinChipText.Text ?? string.Empty;
    internal string DevicesSummaryValue => DevicesSummaryText.Text ?? string.Empty;
    internal int AttentionCards => _attention.Count;
    internal string AttentionSubtitleValue => AttentionSubtitle.Text ?? string.Empty;
    internal bool AttentionEmptyShown => AttentionEmpty.IsVisible;
    internal bool ActivityEmptyShown => ActivityEmpty.IsVisible;
    internal string ActivityEmptyValue => ActivityEmptyText.Text ?? string.Empty;
    internal string ActivityNoMatchValue => ActivityNoMatch.IsVisible ? ActivityNoMatch.Text ?? string.Empty : string.Empty;
    internal ActivityFilter ActivityFilterNow => _activityFilter;
    internal string ActivityPillText(ActivityFilter filter) => ActivityView.Label(filter) + " " + _pills[filter].Count.Text;
    internal bool DeletedPaneShown => DeletedPane.IsVisible;
    internal bool FilesPaneShown => FilesPane.IsVisible;
    internal bool AccountShown => AccountPage.IsVisible;
    internal AccountPageView Account => AccountPage;
    internal bool HeaderRowShown => HeaderRow.IsVisible;
    internal string DeletedCountValue => DeletedCount.Text ?? string.Empty;
    internal string HistorySearchWatermark => HistorySearch.Watermark ?? string.Empty;

    internal void SearchActivity(string text) => ActivitySearch.Text = text;

    internal Task RestoreDeletedRowAsync(int index) => RestoreDeletedAsync(_shownFiles[index]);

    /// <summary>Switches to the Devices page (used by the headless UI test).</summary>
    internal void ShowDevicesTab() => Navigate(MainPage.Devices);

    internal void SelectHistoryRow(int index) => FileList.SelectedIndex = index;

    internal void SearchHistory(string text) => HistorySearch.Text = text;

    internal void ShowAllFiles() => FilesTab.IsChecked = true;

    internal Task RestoreFirstVersionAsync() => _versions.Count == 0 ? Task.CompletedTask : RestoreAsync(_versions[0]);

    /// <summary>Clicks a sidebar entry, as a person would.</summary>
    internal void ClickNav(MainPage page) => NavFor(page).IsChecked = true;

    internal bool SettingsShown => SettingsPage.Content is not null;

    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnViewLog(object? sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnReportBug(object? sender, RoutedEventArgs e) => _actions?.ReportBug();
    private void OnUpdateNow(object? sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object? sender, RoutedEventArgs e) => _actions?.DismissUpdate();

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

    private void OnAttentionSecond(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: AttentionItem item })
            item.Second?.Invoke();
    }
}
