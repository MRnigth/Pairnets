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
}

public partial class MainWindow : Window
{
    private readonly IMainActions? _actions;
    private readonly ObservableCollection<ActivityItem> _activity = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];

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
        var (brush, icon) = Visuals.ForStatus(s.Status);
        StatusBadge.Fill = Visuals.Resource<IBrush>(brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
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
            TransferGlyph.Data = Visuals.Resource<Geometry>(s.Operation == "download" ? "I.Down" : "I.Up");
            TransferFile.Text = $"{s.OperationText} {s.CurrentFileName}";
            TransferFolder.Text = s.CurrentFolder.Length > 0 ? s.CurrentFolder : "Top folder";
            TransferCount.Text = s.FileCountText;
            TransferProgress.IsIndeterminate = s.Percent is null;
            TransferProgress.Value = s.Percent ?? 0;
            TransferBytes.Text = s.ProgressText;
            TransferBytes.IsVisible = s.ProgressText.Length > 0;
        }
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activity.Clear();
        foreach (var item in items)
            _activity.Add(item);
        UpdatePanels();
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

    private void OnTabChanged(object? sender, RoutedEventArgs e) => UpdatePanels();
    private void OnFix(object? sender, RoutedEventArgs e) => _actions?.FixBlocked();
    private void OnSyncNow(object? sender, RoutedEventArgs e) => _actions?.SyncNow();
    private void OnPause(object? sender, RoutedEventArgs e) => _actions?.TogglePause();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _actions?.OpenFolder();
    private void OnSettings(object? sender, RoutedEventArgs e) => _actions?.ShowSettings();
    private void OnViewLog(object? sender, RoutedEventArgs e) => _actions?.ViewLog();

    private void OnAttentionAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: AttentionItem item })
            item.Action();
    }
}
