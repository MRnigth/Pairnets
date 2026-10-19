using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// The quick look that opens from the tray (Linux) or the menu bar (macOS): status, the nest with a small
/// branch per computer, the latest changes and the main buttons. It hides when it loses focus.
/// </summary>
public partial class TrayPanel : Window
{
    private readonly ITrayActions? _actions;
    private StatusSnapshot _status = StatusSnapshot.Initial;
    private string? _device;
    private IReadOnlyList<ActivityItem> _items = [];
    private IReadOnlyList<TrayChange> _shown = [];
    private int _attention;
    private string? _drawnKey;
    private bool _menuOpen;

    public TrayPanel()
        : this(null)
    {
    }

    public TrayPanel(ITrayActions? actions)
    {
        InitializeComponent();
        _actions = actions;
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

    private static void Paint(Control target, AvaloniaProperty property, string key) => target.Bind(property, target.GetResourceObservable(key));

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
        Paint(StatusBadge, Shape.FillProperty, fill);
        Paint(StatusGlyph, LineIcon.StrokeProperty, ink);
        StatusGlyph.Data = Visuals.Resource<Geometry>(glyph);
        StatusGlyph.Width = StatusGlyph.Height = size;
        StatusGlyph.StrokeThickness = stroke;
        StatusGlyph.Classes.Set("spin-slow", m.Tone == TrayTone.Syncing);

        Headline.Text = m.Headline;
        Detail.Text = m.Detail;
        ToolTip.SetTip(Detail, m.Detail);

        PercentText.IsVisible = Progress.IsVisible = m.ShowsProgress;
        ProgressLine.IsVisible = m.ShowsProgress && (m.FilesText is not null || m.LeftText is not null);
        if (m.Percent is { } percent)
        {
            PercentText.Text = percent + "%";
            Progress.Value = percent;
        }
        FilesText.Text = m.FilesText ?? string.Empty;
        LeftText.Text = m.LeftText ?? string.Empty;
        PausedNote.IsVisible = m.PausedNote is not null;
        PausedNoteText.Text = m.PausedNote ?? string.Empty;

        // The nest and its branches
        NestTitle.Text = m.NestTitle;
        NestSub.Text = m.NestSub;
        Paint(NestDotShape, Shape.FillProperty, m.NestDot switch
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
        MoreText.IsVisible = m.MoreText is not null;
        MoreText.Text = m.MoreText is null ? string.Empty : m.MoreText + " computers";

        // Buttons
        FixButton.IsVisible = m.FixLabel is not null;
        FixButton.Content = m.FixLabel;
        var attention = TrayPanelModel.AttentionText(_attention);
        AttentionButton.IsVisible = attention is not null;
        AttentionText.Text = attention ?? string.Empty;
        PauseText.Text = m.PauseLabel;
        PauseGlyph.Data = Visuals.Resource<Geometry>(m.Paused ? "I.Play" : "I.Pause");
        PauseButton.Classes.Set("accent", m.ResumeIsPrimary);
        PauseButton.Classes.Set("subtle", !m.ResumeIsPrimary);
        OpenButton.Classes.Set("accent", !m.ResumeIsPrimary);
        OpenButton.Classes.Set("subtle", m.ResumeIsPrimary);
        if (m.ResumeIsPrimary)
            PauseGlyph.Stroke = Brushes.White;
        else
            Paint(PauseGlyph, LineIcon.StrokeProperty, "T.Text");

        // Latest changes
        var changes = TrayPanelModel.Changes(_status, _items, m.ChangesLimit(attention is not null), now);
        if (!_shown.SequenceEqual(changes))
        {
            _shown = changes;
            TrayBranches.FillChanges(RecentList, changes);
        }
        RecentEmpty.IsVisible = changes.Count == 0;
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Headline.Text ?? string.Empty;
    internal int RecentRows => RecentList.Children.Count;
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
    private void OnSeeAll(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Activity));
    private void OnAttention(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Attention));
    private void OnSettings(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.Settings));
    private void OnHistory(object? sender, RoutedEventArgs e) => Run(a => a.OpenWindow(MainPage.History));
    private void OnViewLog(object? sender, RoutedEventArgs e) => Run(a => a.ViewLog());
    private void OnReportBug(object? sender, RoutedEventArgs e) => Run(a => a.ReportBug());
    private void OnQuit(object? sender, RoutedEventArgs e) => Run(a => a.Quit());
}
