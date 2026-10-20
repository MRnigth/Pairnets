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
/// OneDrive or Dropbox flyout): status, the nest with a small
/// branch per computer, the latest changes and the main buttons. It hides when it loses focus.
/// </summary>
public partial class TrayPanel : Window
{
    private readonly ITrayActions? _actions;
    private readonly ObservableCollection<TrayChange> _recent = [];
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private string? _device;
    private IReadOnlyList<ActivityItem> _items = [];
    private int _attention;
    private string? _drawnKey;
    private bool _menuOpen;

    public TrayPanel(ITrayActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _actions = actions;
        Logo.Source = AppIcons.Large;
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
            else
                Redraw();
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
        _status = s;
        _device = device;
        Redraw();
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _items = items;
        Redraw();
    }

    public void ShowAttention(int count)
    {
        _attention = count;
        Redraw();
    }

    private void Redraw()
    {
        var now = DateTimeOffset.UtcNow;
        var m = TrayPanelModel.Build(_status, _device, now);

        // The status circle
        var (fill, ink, glyph, size, stroke) = m.Tone switch
        {
            TrayTone.Ok => ("T.OkPill", "T.OkText", "I.Check", 22, 2.6),
            TrayTone.Paused => ("T.WarnPill", "T.WarnText", "I.Pause", 20, 2.6),
            TrayTone.Warn => ("T.WarnPill", "T.WarnText", "I.Warning", 22, 2.2),
            TrayTone.Bad => ("T.BadPill", "T.BadText", "I.CloudOff", 22, 2.2),
            _ => ("T.AccentSoft", "T.AccentInk", "I.Sync", 20, 2.2),
        };
        StatusBadge.SetResourceReference(Shape.FillProperty, fill);
        StatusGlyph.SetResourceReference(LineIcon.StrokeProperty, ink);
        StatusGlyph.Data = Visuals.Resource<Geometry>(glyph);
        StatusGlyph.Width = StatusGlyph.Height = size;
        StatusGlyph.StrokeThickness = stroke;
        Motion.Spin(StatusGlyph, IsVisible && m.Tone == TrayTone.Syncing, slow: true);

        Headline.Text = m.Headline;
        Detail.Text = m.Detail;
        Detail.ToolTip = m.Detail;

        PercentText.Visibility = Show(m.ShowsProgress);
        Progress.Visibility = Show(m.ShowsProgress);
        ProgressLine.Visibility = Show(m.ShowsProgress && (m.FilesText is not null || m.LeftText is not null));
        if (m.Percent is { } percent)
        {
            PercentText.Text = percent + "%";
            Motion.Glide(Progress, percent);
        }
        FilesText.Text = m.FilesText ?? string.Empty;
        LeftText.Text = m.LeftText ?? string.Empty;
        PausedNote.Visibility = Show(m.PausedNote is not null);
        PausedNoteText.Text = m.PausedNote ?? string.Empty;

        // The nest and its branches
        NestTitle.Text = m.NestTitle;
        NestSub.Text = m.NestSub;
        NestDotShape.SetResourceReference(Shape.FillProperty, m.NestDot switch
        {
            NestDot.Bad => "T.Bad",
            NestDot.Warn => "T.Warn",
            NestDot.Off => "T.Off",
            _ => "T.Ok",
        });
        if (_drawnKey != m.BranchKey)
        {
            _drawnKey = m.BranchKey;
            TrayBranches.Fill(Branches, m);
        }
        MoreText.Visibility = Show(m.MoreText is not null);
        MoreText.Text = m.MoreText is null ? string.Empty : m.MoreText + " computers";

        // Buttons
        FixButton.Visibility = Show(m.FixLabel is not null);
        FixButton.Content = m.FixLabel;
        var attention = TrayPanelModel.AttentionText(_attention);
        AttentionButton.Visibility = Show(attention is not null);
        AttentionText.Text = attention ?? string.Empty;
        PauseText.Text = m.PauseLabel;
        PauseGlyph.Data = Visuals.Resource<Geometry>(m.Paused ? "I.Play" : "I.Pause");
        PauseButton.Style = (Style)FindResource(m.ResumeIsPrimary ? "AccentButton" : "SubtleButton");
        OpenButton.Style = (Style)FindResource(m.ResumeIsPrimary ? "SubtleButton" : "AccentButton");

        // Latest changes
        var changes = TrayPanelModel.Changes(_status, _items, m.ChangesLimit(attention is not null), now);
        if (!_recent.SequenceEqual(changes))
        {
            _recent.Clear();
            foreach (var change in changes)
                _recent.Add(change);
        }
        RecentEmpty.Visibility = Show(_recent.Count == 0);
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

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
    private void OnSeeAll(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Activity));
    private void OnAttention(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Attention));
    private void OnSettings(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Settings));
    private void OnHistory(object sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.History));
    private void OnViewLog(object sender, RoutedEventArgs e) => Run(a => a.ViewLog());
    private void OnReportBug(object sender, RoutedEventArgs e) => Run(a => a.ReportBug());
    private void OnQuit(object sender, RoutedEventArgs e) => Run(a => a.Quit());
}
