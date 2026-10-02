using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

/// <summary>An item in the "Needs attention" list with one action button.</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action);

/// <summary>What the main window's buttons do (implemented by <see cref="DesktopController"/>).</summary>
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

public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<ActivityItem> _activity = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];
    private readonly ObservableCollection<ActiveFileView> _active = [];
    private string? _shownState;

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(IMainActions? actions)
    {
        InitializeComponent();
        _actions = actions;
        ActivityList.ItemsSource = _activity;
        AttentionList.ItemsSource = _attention;
        ActiveList.ItemsSource = _active;
        UpdatePanels();
    }

    /// <summary>Set when the app exits, so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && !e.IsProgrammatic)
        {
            e.Cancel = true; // keep running in the menu bar / tray
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowStatus(StatusSnapshot s, string? folder)
    {
        var (brush, icon) = s.IsWaiting ? ("S.Grey", "I.Wait") : Visuals.ForStatus(s.Status);
        StatusBadge.Fill = Visuals.Resource<IBrush>(brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
        Animate(s);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText;
        Detail.IsVisible = s.DetailText.Length > 0;
        LastSync.Text = s.LastSyncText;
        FolderPill.IsVisible = folder is not null;
        FolderText.Text = folder ?? string.Empty;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;

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

        // Free space on the server; a warning colour when it runs low.
        ServerPill.IsVisible = s.ServerFreeText is not null;
        ServerText.Text = s.ServerFreeText ?? string.Empty;
        ServerPill.Classes.Set("warn", s.ServerSpaceLow);

        // Waiting for the other computer's big batch.
        WaitPanel.IsVisible = s.IsWaiting;
        if (s.IsWaiting)
        {
            WaitProgress.IsIndeterminate = s.WaitingPercent is null;
            WaitProgress.Value = s.WaitingPercent ?? 0;
            WaitText.Text = s.WaitingProgressText;
        }
    }

    /// <summary>
    /// Shows or hides the update banner. <paramref name="progress"/> 0–100 while downloading, null otherwise.
    /// </summary>
    public void ShowUpdate(string? title, string detail, string button, double? progress = null, bool busy = false)
    {
        UpdateBanner.IsVisible = title is not null;
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
        LiveLists.Sync(_activity, items);
        UpdatePanels();
    }

    /// <summary>
    /// Motion for the status badge: the sync glyph turns while syncing, the badge breathes while
    /// waiting for the other computer, and it pops once whenever the state changes.
    /// </summary>
    private void Animate(StatusSnapshot s)
    {
        StatusGlyph.Classes.Set("spin", s.Status == Tether.Core.Sync.RunnerStatus.Syncing && !s.IsWaiting);
        StatusBadgeHost.Classes.Set("pulse", s.IsWaiting);
        var state = s.IsWaiting ? "waiting" : s.Status.ToString();
        if (_shownState is not null && _shownState != state && !s.IsWaiting)
        {
            StatusBadgeHost.Classes.Remove("pop");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusBadgeHost.Classes.Add("pop"), Avalonia.Threading.DispatcherPriority.Background);
        }
        _shownState = state;
    }

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionBadge.IsVisible = items.Count > 0;
        AttentionCount.Text = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        var showAttention = AttentionTab.IsChecked == true;
        ActivityPanel.IsVisible = !showAttention && _activity.Count > 0;
        ActivityEmpty.IsVisible = !showAttention && _activity.Count == 0;
        AttentionPanel.IsVisible = showAttention && _attention.Count > 0;
        AttentionEmpty.IsVisible = showAttention && _attention.Count == 0;
    }

    /// <summary>Switches to the "Needs attention" list (used by notifications and screenshots).</summary>
    public void ShowAttentionTab(bool attention)
    {
        AttentionTab.IsChecked = attention;
        ActivityTab.IsChecked = !attention;
        UpdatePanels();
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Headline.Text ?? string.Empty;
    internal bool FixVisible => FixButton.IsVisible;
    internal int ActivityCount => _activity.Count;
    internal bool TransferVisible => TransferCard.IsVisible;
    internal bool GlyphSpins => StatusGlyph.Classes.Contains("spin");
    internal bool BadgePulses => StatusBadgeHost.Classes.Contains("pulse");
    internal string ArrowMotion => TransferArrow.Classes.Contains("rise") ? "rise" : TransferArrow.Classes.Contains("fall") ? "fall" : "none";
    internal int ActiveRows => _active.Count;

    private void OnTabChanged(object? sender, RoutedEventArgs e) => UpdatePanels();
    private void OnFix(object? sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnSettings(object? sender, RoutedEventArgs e) => _actions?.ShowSettings();
    private void OnViewLog(object? sender, RoutedEventArgs e) => _actions?.ViewLog();
    private void OnUpdateNow(object? sender, RoutedEventArgs e) => _actions?.UpdateNow();
    private void OnUpdateLater(object? sender, RoutedEventArgs e) => _actions?.DismissUpdate();
    private void OnDownloadNow(object? sender, RoutedEventArgs e) => _actions?.DownloadNow();

    private void OnAttentionAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: AttentionItem item })
            item.Action();
    }
}
