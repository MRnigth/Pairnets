using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
/// OneDrive or Dropbox flyout): status, the two computers, the transfer, recent changes and the
/// main buttons. It hides when it loses focus.
/// </summary>
public partial class TrayPanel : Window
{
    private readonly ITrayActions? _actions;
    private readonly ObservableCollection<ActivityItem> _recent = [];
    private string? _tintKey;
    private bool _menuOpen;

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
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                Motion.Spin(StatusGlyph, false);
        };
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
        var (brush, icon) = s.IsWaiting ? ("S.Grey", "I.Wait") : Visuals.ForStatus(s.Status);
        StatusBadge.SetResourceReference(Shape.FillProperty, brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
        Motion.Spin(StatusGlyph, IsVisible && s.Status == Pairnets.Core.Sync.RunnerStatus.Syncing && !s.IsWaiting);
        if (_tintKey != brush)
        {
            _tintKey = brush;
            Head.Background = Visuals.Wash(brush, 0x26, 1);
        }
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText.Length > 0 ? s.DetailText : s.LastSyncText;
        FixButton.Visibility = s.FixLabel is not null ? Visibility.Visible : Visibility.Collapsed;
        FixButton.Content = s.FixLabel;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        TransferCard.Visibility = s.IsTransferring ? Visibility.Visible : Visibility.Collapsed;
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            TransferSpeed.Text = s.BytesPerSecond >= 1 ? Format.Speed(s.BytesPerSecond) : string.Empty;
            Motion.Glide(TransferProgress, s.OverallPercent ?? 0);
            TransferOverall.Text = s.OverallText;
            var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.SetResourceReference(LineIcon.StrokeProperty, uploading ? "S.Green" : "S.Blue");
        }
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        LiveLists.Sync(_recent, items.Take(4).ToList());
        RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowAttention(int count)
    {
        AttentionButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttentionText.Text = count == 1 ? "1 thing needs your attention" : $"{count} things need your attention";
    }

    private void Run(Action<ITrayActions> action)
    {
        Hide();
        if (_actions is not null)
            action(_actions);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            _menuOpen = true;
            menu.PlacementTarget = MoreButton;
            menu.IsOpen = true;
        }
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
    private void OnAttention(object sender, MouseButtonEventArgs e) => Run(a => a.OpenWindow(MainPage.Attention));
    private void OnSettings(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Settings));
    private void OnHistory(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.History));
    private void OnViewLog(object sender, RoutedEventArgs e) => Run(a => a.ViewLog());
    private void OnReportBug(object sender, RoutedEventArgs e) => Run(a => a.ReportBug());
    private void OnQuit(object sender, RoutedEventArgs e) => Run(a => a.Quit());
}
