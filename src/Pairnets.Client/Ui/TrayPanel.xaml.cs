using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>What the tray panel can do besides the main window's actions.</summary>
public interface ITrayActions : IMainActions
{
    /// <summary>Opens the main window on <paramref name="page"/>.</summary>
    void OpenWindow(MainPage page);

    void Quit();
}

/// <summary>
/// The quick look that opens when the tray icon is clicked, next to the taskbar (like the
/// OneDrive or Dropbox flyout): the ring and status, the computers, the transfer, recent changes
/// and the main buttons. It hides when it loses focus. Same design as the Mac/Linux app.
/// </summary>
public partial class TrayPanel : Window
{
    private readonly ITrayActions? _actions;
    private readonly ObservableCollection<object> _recent = [];
    private bool _menuOpen;
    private bool _ringSpins;
    private bool _ringBreathes;
    private bool? _arrowUp;

    public TrayPanel(ITrayActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        Map.Compact = true;
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
            if (e.Key == Key.Escape)
                Hide();
        };
        IsVisibleChanged += (_, _) => Animate(); // nothing moves while the panel is hidden
    }

    /// <summary>When it last hid itself (a click on the tray icon right after should not reopen it).</summary>
    public DateTime HiddenAt { get; private set; }

    /// <summary>
    /// Shows the panel next to the tray: above a bottom taskbar (beside a side one, below a top
    /// one), lined up with where the icon was clicked. <paramref name="anchor"/> and the work area
    /// are in screen pixels.
    /// </summary>
    public void Open(System.Drawing.Point anchor, System.Drawing.Rectangle bounds, System.Drawing.Rectangle workArea)
    {
        Left = -10000; // shown off screen first, so its size and the screen's scale are known
        Top = -10000;
        Show();
        UpdateLayout();
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var a = toDip.Transform(new Point(anchor.X, anchor.Y));
        var area = new Rect(toDip.Transform(new Point(workArea.Left, workArea.Top)), toDip.Transform(new Point(workArea.Right, workArea.Bottom)));
        var w = ActualWidth;
        var h = ActualHeight;
        const double gap = 2;
        double x, y;
        if (workArea.Bottom < bounds.Bottom || (workArea.Top == bounds.Top && workArea.Left == bounds.Left && workArea.Right == bounds.Right))
        {
            x = a.X - w / 2; // taskbar at the bottom (or hidden): above it
            y = area.Bottom - h - gap;
        }
        else if (workArea.Top > bounds.Top)
        {
            x = a.X - w / 2;
            y = area.Top + gap;
        }
        else if (workArea.Left > bounds.Left)
        {
            x = area.Left + gap;
            y = a.Y - h / 2;
        }
        else
        {
            x = area.Right - w - gap;
            y = a.Y - h / 2;
        }
        Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));
        Activate();
    }

    public void ShowStatus(StatusSnapshot s, string? device)
    {
        var ring = OverviewRing.From(s);
        Ring.SetResourceReference(ProgressRing.RingBrushProperty, ring.BrushKey);
        Ring.GlideTo(ring.Spins ? 25 : ring.Percent);
        _ringSpins = ring.Spins;
        _ringBreathes = ring.Breathes;
        RingText.Text = ring.CenterText ?? string.Empty;
        RingText.Visibility = Show(ring.CenterText is not null);
        RingIcon.Visibility = Show(ring.CenterText is null && ring.IconKey is not null && !ring.Spins);
        if (ring.IconKey is not null)
            RingIcon.Data = Visuals.Resource<Geometry>(ring.IconKey);
        RingIcon.SetResourceReference(LineIcon.StrokeProperty, ring.BrushKey);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText.Length > 0 && !s.IsTransferring ? s.DetailText : s.LastSyncText;
        var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
        SpeedLine.Visibility = Show(s.IsTransferring && s.BytesPerSecond >= 1);
        SpeedLine.Text = (uploading ? "↑ " : "↓ ") + Format.Speed(s.BytesPerSecond);
        FixButton.Visibility = Show(s.FixLabel is not null);
        FixButton.Content = s.FixLabel;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        TransferCard.Visibility = Show(s.IsTransferring);
        _arrowUp = s.IsTransferring ? uploading : null;
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            var speed = s.SpeedText;
            var left = speed.IndexOf(" · ", StringComparison.Ordinal);
            TransferSpeed.Text = left >= 0 ? speed[(left + 3)..] : string.Empty; // the speed itself is under the headline
            TransferProgress.IsIndeterminate = s.OverallPercent is null;
            Motion.Glide(TransferProgress, s.OverallPercent ?? 0);
            TransferOverall.Text = s.OverallText;
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
        }
        Animate();
    }

    /// <summary>The ring turns while syncing without a number and breathes while waiting; the arrow travels with the files.</summary>
    private void Animate()
    {
        Motion.Spin(Ring, IsVisible && _ringSpins);
        Motion.Pulse(RingHost, IsVisible && _ringBreathes);
        Motion.Travel(TransferArrow, IsVisible ? _arrowUp : null);
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        LiveLists.Sync(_recent, ActivityGroups.Recent(items, 4));
        RecentEmpty.Visibility = Show(_recent.Count == 0);
    }

    public void ShowAttention(int count)
    {
        AttentionButton.Visibility = Show(count > 0);
        AttentionText.Text = count == 1 ? "1 thing needs your attention" : $"{count} things need your attention";
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Run(Action<ITrayActions> action)
    {
        Hide();
        if (_actions is not null)
            action(_actions);
    }

    /// <summary>The "…" menu opens under the button, its right edge lined up with the button's.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu)
            return;
        _menuOpen = true;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 10, target.Height + 4), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        if (IsVisible && !IsActive)
            Activate();
    }

    private void OnFix(object sender, RoutedEventArgs e) => Run(a => a.FixBlocked());
    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => Run(a => a.OpenFolder());
    private void OnOpenApp(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Overview));
    private void OnAttention(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Attention));
    private void OnSettings(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Settings));
    private void OnHistory(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.History));
    private void OnViewLog(object sender, RoutedEventArgs e) => Run(a => a.ViewLog());
    private void OnReportBug(object sender, RoutedEventArgs e) => Run(a => a.ReportBug());
    private void OnQuit(object sender, RoutedEventArgs e) => Run(a => a.Quit());
}
