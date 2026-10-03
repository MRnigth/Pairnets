using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Tether.Client.Themes;
using Tether.Core.Client;

namespace Tether.Client.Ui;

/// <summary>An item in the "Needs attention" list with one action button.</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action);

/// <summary>What the main window's buttons do (implemented by <see cref="TrayController"/>).</summary>
public interface IMainActions
{
    void FixBlocked();
    void SyncNow();
    void TogglePause();
    void OpenFolder();
    void ShowSettings();
    void ViewLog();
    void UpdateNow();
    void DismissUpdate();
    void DownloadNow();
}

/// <summary>
/// The main window: status, current transfer, recent activity and things needing attention.
/// It only displays what <see cref="ClientSession"/> reports and forwards button clicks.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<ActivityItem> _activity = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<ActiveFileView> _active = [];
    private string? _shownState;

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        _actions = actions;
        ActivityList.ItemsSource = _activity;
        AttentionList.ItemsSource = _attention;
        ActiveList.ItemsSource = _active;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopMotion(); // nothing moves (or costs CPU) while Tether sits in the tray
        };
        UpdatePanels();
    }

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowStatus(StatusSnapshot s, string? folder)
    {
        var (brush, icon) = s.IsWaiting ? ("S.Grey", "I.Wait") : Visuals.ForStatus(s.Status);
        StatusBadge.SetResourceReference(Shape.FillProperty, brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
        Animate(s);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText;
        Detail.Visibility = Show(s.DetailText.Length > 0);
        LastSync.Text = s.LastSyncText;
        FolderPill.Visibility = Show(folder is not null);
        FolderText.Text = folder ?? string.Empty;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.Visibility = Show(s.FixLabel is not null);
        FixButton.Content = s.FixLabel;

        TransferCard.Visibility = Show(s.IsTransferring);
        if (!s.IsTransferring)
            Motion.Travel(TransferArrow, null);
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            TransferSpeed.Text = s.SpeedText;
            LimitPill.Visibility = Show(s.LimitText is not null);
            LimitText.Text = s.LimitText ?? string.Empty;
            Motion.Glide(TransferProgress, s.OverallPercent ?? 0);
            TransferOverall.Text = s.OverallText;
            var several = s.Active.Count > 1 || s.FilesTotal > 1;
            ActiveDivider.Visibility = Show(several && s.Active.Count > 0);
            ActiveList.Visibility = Show(several);
            LiveLists.Sync(_active, s.Active);
            var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.SetResourceReference(LineIcon.StrokeProperty, uploading ? "S.Green" : "S.Blue");
            Motion.Travel(TransferArrow, uploading);
        }

        // Connected to the server (or not).
        ConnectionPill.Visibility = Show(s.ConnectionText is not null);
        ConnectionText.Text = s.ConnectionText ?? string.Empty;
        ConnectionIcon.Data = Visuals.Resource<Geometry>(s.IsConnected ? "I.CloudCheck" : "I.CloudOff");
        ConnectionIcon.SetResourceReference(LineIcon.StrokeProperty, s.IsConnected ? "S.Green" : "T.WarnText");
        ConnectionPill.SetResourceReference(Border.BackgroundProperty, s.IsConnected ? "T.Pill" : "T.WarnPill");
        ConnectionText.SetResourceReference(TextBlock.ForegroundProperty, s.IsConnected ? "T.Text" : "T.WarnText");

        // Free space on the server; a warning colour when it runs low.
        ServerPill.Visibility = Show(s.ServerFreeText is not null);
        ServerText.Text = s.ServerFreeText ?? string.Empty;
        ServerPill.SetResourceReference(Border.BackgroundProperty, s.ServerSpaceLow ? "T.WarnPill" : "T.Pill");
        ServerText.SetResourceReference(TextBlock.ForegroundProperty, s.ServerSpaceLow ? "T.WarnText" : "T.Text");

        // Waiting for the other computer's big batch.
        WaitPanel.Visibility = Show(s.IsWaiting);
        if (s.IsWaiting)
        {
            Motion.Glide(WaitProgress, s.WaitingPercent ?? 0);
            WaitText.Text = s.WaitingProgressText;
        }
    }

    /// <summary>Shows or hides the update banner. <paramref name="progress"/> 0–100 while downloading.</summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        UpdateBanner.Visibility = Show(title is not null);
        Motion.Bob(UpdateIcon, title is not null && !busy);
        if (title is null)
            return;
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateButton.Content = button;
        UpdateButton.IsEnabled = !busy;
        UpdateLater.Visibility = Show(!busy);
        UpdateProgress.Visibility = Show(progress is not null);
        Motion.Glide(UpdateProgress, progress ?? 0);
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        LiveLists.Sync(_activity, items);
        UpdatePanels();
    }

    /// <summary>
    /// Motion for the status badge: the sync glyph turns while syncing, the badge breathes while
    /// waiting for the other computer, and it pops once whenever the state changes.
    /// </summary>
    private void Animate(StatusSnapshot s)
    {
        Motion.Spin(StatusGlyph, s.Status == Tether.Core.Sync.RunnerStatus.Syncing && !s.IsWaiting);
        Motion.Pulse(StatusBadgeHost, s.IsWaiting);
        var state = s.IsWaiting ? "waiting" : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
            Motion.Pop(StatusBadgeHost);
        _shownState = state;
    }

    private void StopMotion()
    {
        Motion.Spin(StatusGlyph, false);
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

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionBadge.Visibility = Show(items.Count > 0);
        AttentionCount.Text = items.Count.ToString(CultureInfo.InvariantCulture);
        UpdatePanels();
    }

    /// <summary>Switches to the "Needs attention" list (used by notifications and screenshots).</summary>
    public void ShowAttentionTab(bool attention)
    {
        AttentionTab.IsChecked = attention;
        ActivityTab.IsChecked = !attention;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (ActivityPanel is null)
            return; // Checked fires during InitializeComponent
        var showAttention = AttentionTab.IsChecked == true;
        ActivityPanel.Visibility = Show(!showAttention && _activity.Count > 0);
        ActivityEmpty.Visibility = Show(!showAttention && _activity.Count == 0);
        AttentionPanel.Visibility = Show(showAttention && _attention.Count > 0);
        AttentionEmpty.Visibility = Show(showAttention && _attention.Count == 0);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnTabChanged(object sender, RoutedEventArgs e) => UpdatePanels();
    private void OnFix(object sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnSettings(object sender, RoutedEventArgs e) => _actions?.ShowSettings();
    private void OnViewLog(object sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnUpdateNow(object sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object sender, RoutedEventArgs e) => _actions?.DismissUpdate();
    private void OnDownloadNow(object sender, RoutedEventArgs e) => _actions?.DownloadNow();

    private void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AttentionItem item })
            item.Action();
    }
}
