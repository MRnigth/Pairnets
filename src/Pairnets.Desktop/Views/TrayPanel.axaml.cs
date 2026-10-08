using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>What the tray panel can do besides the main window's actions.</summary>
public interface ITrayActions : IMainActions
{
    /// <summary>Opens the main window on <paramref name="page"/>.</summary>
    void OpenWindow(MainPage page);

    void Quit();
}

/// <summary>
/// The quick look that opens from the tray (Linux) or the menu bar (macOS): status, the two
/// computers, the transfer, recent changes and the main buttons. It hides when it loses focus.
/// </summary>
public partial class TrayPanel : Window
{
    private readonly ITrayActions? _actions;
    private readonly ObservableCollection<object> _recent = [];
    private bool _menuOpen;

    public TrayPanel()
        : this(null)
    {
    }

    public TrayPanel(ITrayActions? actions)
    {
        InitializeComponent();
        _actions = actions;
        RecentList.ItemsSource = _recent;
        Deactivated += (_, _) =>
        {
            // Its own "…" menu takes focus for a moment; only a click elsewhere closes the panel.
            if (!_menuOpen)
            {
                HiddenAt = DateTime.UtcNow;
                Hide();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                Hide();
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>When it last hid itself (a click on the tray icon right after should not reopen it).</summary>
    public DateTime HiddenAt { get; private set; }

    /// <summary>Shows the panel in the corner next to the tray or menu bar.</summary>
    public void Open()
    {
        Show();
        Place();
        Activate();
    }

    /// <summary>
    /// Top right under the menu bar (macOS, or a Linux panel at the top), otherwise bottom right
    /// above the taskbar. The icon's own position is not available, so the corner is the best guess.
    /// </summary>
    private void Place()
    {
        if (!IsVisible || Screens.Primary is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)Math.Ceiling(Bounds.Width * scale);
        var h = (int)Math.Ceiling(Bounds.Height * scale);
        var gap = (int)(4 * scale);
        var top = OperatingSystem.IsMacOS() || area.Y > screen.Bounds.Y;
        var x = area.Right - w - gap;
        var y = top ? area.Y + gap : area.Bottom - h - gap;
        Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    public void ShowStatus(StatusSnapshot s, string? device)
    {
        var ring = OverviewRing.From(s);
        Visuals.Bind(Ring, ProgressRing.RingBrushProperty, ring.BrushKey);
        Ring.Value = ring.Spins ? 25 : ring.Percent;
        Ring.Classes.Set("spin", ring.Spins);
        RingText.Text = ring.CenterText ?? string.Empty;
        RingText.IsVisible = ring.CenterText is not null;
        RingIcon.IsVisible = ring.CenterText is null && ring.IconKey is not null && !ring.Spins;
        if (ring.IconKey is not null)
            RingIcon.Data = Visuals.Resource<Geometry>(ring.IconKey);
        Visuals.Bind(RingIcon, LineIcon.StrokeProperty, ring.BrushKey);
        RingHost.Classes.Set("pulse", ring.Breathes);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText.Length > 0 && !s.IsTransferring ? s.DetailText : s.LastSyncText;
        var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
        SpeedLine.IsVisible = s.IsTransferring && s.BytesPerSecond >= 1;
        SpeedLine.Text = (uploading ? "↑ " : "↓ ") + Format.Speed(s.BytesPerSecond);
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        TransferCard.IsVisible = s.IsTransferring;
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            var speed = s.SpeedText;
            var left = speed.IndexOf(" · ", StringComparison.Ordinal);
            TransferSpeed.Text = left >= 0 ? speed[(left + 3)..] : string.Empty; // the speed itself is under the headline
            TransferProgress.IsIndeterminate = s.OverallPercent is null;
            TransferProgress.Value = s.OverallPercent ?? 0;
            TransferOverall.Text = s.OverallText;
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.Classes.Set("rise", uploading);
            TransferArrow.Classes.Set("fall", !uploading);
        }
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        LiveLists.Sync(_recent, ActivityGroups.Recent(items, 4));
        RecentEmpty.IsVisible = _recent.Count == 0;
    }

    public void ShowAttention(int count)
    {
        AttentionButton.IsVisible = count > 0;
        AttentionText.Text = count == 1 ? "1 thing needs your attention" : $"{count} things need your attention";
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Headline.Text ?? string.Empty;
    internal int RecentRows => _recent.Count;
    internal bool AttentionShown => AttentionButton.IsVisible;

    private void Run(Action<ITrayActions> action)
    {
        Hide();
        if (_actions is not null)
            action(_actions);
    }

    private void OnMenuOpened(object? sender, EventArgs e) => _menuOpen = true;

    private void OnMenuClosed(object? sender, EventArgs e)
    {
        _menuOpen = false;
        if (!IsActive)
            Activate();
    }

    private void OnFix(object? sender, RoutedEventArgs e) => Run(a => a.FixBlocked());
    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Run(a => a.OpenFolder());
    private void OnOpenApp(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Overview));
    private void OnAttention(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Attention));
    private void OnSettings(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Settings));
    private void OnHistory(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.History));
    private void OnViewLog(object? sender, RoutedEventArgs e) => Run(a => a.ViewLog());
    private void OnReportBug(object? sender, RoutedEventArgs e) => Run(a => a.ReportBug());
    private void OnQuit(object? sender, RoutedEventArgs e) => Run(a => a.Quit());
}
