using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tether.Core.Client;
using Tether.Core.Sync;

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
        StatusDot.Fill = new SolidColorBrush(s.Status switch
        {
            RunnerStatus.Idle => Color.FromRgb(46, 160, 67),
            RunnerStatus.Syncing => Color.FromRgb(33, 118, 214),
            RunnerStatus.Offline => Color.FromRgb(128, 128, 128),
            RunnerStatus.Paused => Color.FromRgb(214, 160, 33),
            RunnerStatus.Blocked => Color.FromRgb(230, 110, 20),
            _ => Color.FromRgb(207, 34, 46),
        });
        Headline.Text = s.Headline;
        Detail.Text = s.DetailText;
        Detail.IsVisible = s.DetailText.Length > 0;
        LastSync.Text = s.LastSyncText;
        FolderText.Text = folder is null ? string.Empty : "Folder: " + folder;
        PauseButton.Content = s.Paused ? "Resume" : "Pause";
        FixButton.IsVisible = s.FixLabel is not null;
        FixButton.Content = s.FixLabel;

        TransferPanel.IsVisible = s.IsTransferring;
        if (s.IsTransferring)
        {
            TransferFile.Text = $"{(s.Operation == "download" ? "Downloading" : s.Operation == "upload" ? "Uploading" : "Working on")} {s.CurrentPath}";
            TransferCount.Text = s.FilesTotal > 0 ? $"{Math.Min(s.FilesDone + 1, s.FilesTotal)} of {s.FilesTotal}" : string.Empty;
            TransferProgress.IsIndeterminate = s.Percent is null;
            TransferProgress.Value = s.Percent ?? 0;
        }
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activity.Clear();
        foreach (var item in items)
            _activity.Add(item);
        ActivityEmpty.IsVisible = _activity.Count == 0;
    }

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionTab.Header = items.Count == 0 ? "Needs attention" : $"Needs attention ({items.Count})";
        AttentionEmpty.IsVisible = items.Count == 0;
    }

    // Exposed for the headless UI test.
    internal string HeadlineText => Headline.Text ?? string.Empty;
    internal bool FixVisible => FixButton.IsVisible;
    internal int ActivityCount => _activity.Count;
    internal bool TransferVisible => TransferPanel.IsVisible;

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
