using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

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
    private readonly ObservableCollection<ActivityItem> _recent = [];
    private readonly StatusMoments _moments = new();
    private string? _tintKey;
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

    /// <summary>Shows the panel in the corner next to the tray or menu bar, sliding in from its edge.</summary>
    public void Open()
    {
        // Added right away (not on the next tick like Motion.Once), so the first frame is already the start of the slide.
        Card.Classes.Add(Screens.Primary is { } screen && AtTop(screen) ? "arrive-down" : "arrive-up");
        Show();
        Place();
        Activate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || IsVisible)
            return;
        _moments.Reset(); // nothing celebrates late when the panel opens again
        Card.Classes.Remove("arrive-up");
        Card.Classes.Remove("arrive-down");
    }

    /// <summary>The menu bar (macOS) or a Linux panel is at the top of the screen.</summary>
    private static bool AtTop(Screen screen) => OperatingSystem.IsMacOS() || screen.WorkingArea.Y > screen.Bounds.Y;

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
        var top = AtTop(screen);
        var x = area.Right - w - gap;
        var y = top ? area.Y + gap : area.Bottom - h - gap;
        Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    public void ShowStatus(StatusSnapshot s, string? device)
    {
        var (brush, icon) = s.IsWaiting ? ("S.Grey", "I.Wait") : Visuals.ForStatus(s.Status);
        StatusBadge.Fill = Visuals.Resource<IBrush>(brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
        StatusGlyph.Classes.Set("spin", s.Status == Tether.Core.Sync.RunnerStatus.Syncing && !s.IsWaiting);
        StatusBadgeHost.Classes.Set("pulse", s.IsWaiting);
        if (_moments.Next(s) != StatusMoment.None && IsVisible)
            Motion.Once(StatusRipple, "ripple"); // a sync that moved files is done
        Tint(brush);
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText.Length > 0 ? s.DetailText : s.LastSyncText;
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;
        PauseText.Text = s.Paused ? "Resume" : "Pause";
        PauseGlyph.Data = Visuals.Resource<Geometry>(s.Paused ? "I.Play" : "I.Pause");
        Map.Show(DeviceMap.Build(s, device, DateTimeOffset.UtcNow));

        TransferCard.IsVisible = s.IsTransferring;
        if (s.IsTransferring)
        {
            TransferTitle.Text = s.BatchTitle;
            TransferSpeed.Text = s.BytesPerSecond >= 1 ? Format.Speed(s.BytesPerSecond) : string.Empty;
            TransferProgress.IsIndeterminate = s.OverallPercent is null;
            TransferProgress.Value = s.OverallPercent ?? 0;
            TransferOverall.Text = s.OverallText;
            var uploading = s.Active.Count == 0 ? s.Operation != "download" : s.Active.Any(a => a.IsUpload);
            TransferArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
            TransferArrow.Stroke = Visuals.Resource<IBrush>(uploading ? "S.Green" : "S.Blue");
            TransferArrow.Classes.Set("rise", uploading);
            TransferArrow.Classes.Set("fall", !uploading);
        }
    }

    private void Tint(string brushKey)
    {
        if (_tintKey == brushKey)
            return;
        _tintKey = brushKey;
        var color = (Visuals.Resource<IBrush>(brushKey) as ISolidColorBrush)?.Color ?? Colors.Gray;
        Head.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x26, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
            },
        };
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        LiveLists.Sync(_recent, items.Take(4).ToList());
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
    internal int Ripples => Motion.Plays(StatusRipple, "ripple");
    internal bool Arriving => Card.Classes.Contains("arrive-up") || Card.Classes.Contains("arrive-down");

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
