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

    public MainWindow(IMainActions? actions)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        _actions = actions;
        ActivityList.ItemsSource = _activity;
        AttentionList.ItemsSource = _attention;
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
        var (brush, icon) = Visuals.ForStatus(s.Status);
        StatusBadge.SetResourceReference(Shape.FillProperty, brush);
        StatusGlyph.Data = Visuals.Resource<Geometry>(icon);
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
        if (s.IsTransferring)
        {
            TransferGlyph.Data = Visuals.Resource<Geometry>(s.Operation == "download" ? "I.Down" : "I.Up");
            TransferFile.Text = $"{s.OperationText} {s.CurrentFileName}";
            TransferFolder.Text = s.CurrentFolder.Length > 0 ? s.CurrentFolder : "Top folder";
            TransferCount.Text = s.FileCountText;
            TransferProgress.IsIndeterminate = s.Percent is null;
            TransferProgress.Value = s.Percent ?? 0;
            TransferBytes.Text = s.ProgressText;
            TransferBytes.Visibility = Show(s.ProgressText.Length > 0);
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

    private void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AttentionItem item })
            item.Action();
    }
}
