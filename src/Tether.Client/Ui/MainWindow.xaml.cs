using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Tether.Core.Client;
using Tether.Core.Sync;

namespace Tether.Client.Ui;

/// <summary>An item in the "Needs attention" list with one action button.</summary>
public sealed record AttentionItem(string Title, string Detail, string ActionLabel, Action Action);

/// <summary>
/// The main window: status, current transfer, recent activity and things needing attention.
/// It only displays what <see cref="ClientSession"/> reports and forwards button clicks.
/// </summary>
public partial class MainWindow : Window
{
    private readonly TrayController _controller;
    private readonly ObservableCollection<ActivityItem> _activity = [];
    private readonly ObservableCollection<AttentionItem> _attention = [];

    public MainWindow(TrayController controller)
    {
        InitializeComponent();
        _controller = controller;
        ActivityList.ItemsSource = _activity;
        AttentionList.ItemsSource = _attention;
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
        Detail.Text = s.Text;
        LastSync.Text = s.LastSyncText;
        FolderText.Text = folder is null ? string.Empty : "Folder: " + folder;
        PauseButton.Content = s.Paused ? "Resume" : "Pause";

        FixButton.Visibility = s.FixLabel is null ? Visibility.Collapsed : Visibility.Visible;
        FixButton.Content = s.FixLabel;

        if (s.IsTransferring)
        {
            TransferPanel.Visibility = Visibility.Visible;
            TransferFile.Text = $"{(s.Operation == "download" ? "Downloading" : s.Operation == "upload" ? "Uploading" : "Working on")} {s.CurrentPath}";
            TransferCount.Text = s.FilesTotal > 0 ? $"{Math.Min(s.FilesDone + 1, s.FilesTotal)} of {s.FilesTotal}" : string.Empty;
            TransferProgress.IsIndeterminate = s.Percent is null;
            TransferProgress.Value = s.Percent ?? 0;
        }
        else
        {
            TransferPanel.Visibility = Visibility.Collapsed;
        }
    }

    public void ShowActivity(IReadOnlyList<ActivityItem> items)
    {
        _activity.Clear();
        foreach (var item in items)
            _activity.Add(item);
        ActivityEmpty.Visibility = _activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowAttention(IReadOnlyList<AttentionItem> items)
    {
        _attention.Clear();
        foreach (var item in items)
            _attention.Add(item);
        AttentionTab.Header = items.Count == 0 ? "Needs attention" : $"Needs attention ({items.Count})";
        AttentionEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFix(object sender, RoutedEventArgs e) => _controller.FixBlocked();
    private void OnSyncNow(object sender, RoutedEventArgs e) => _controller.SyncNow();
    private void OnPause(object sender, RoutedEventArgs e) => _controller.TogglePause();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _controller.OpenFolder();
    private void OnSettings(object sender, RoutedEventArgs e) => _controller.ShowSettings(firstRun: false);
    private void OnViewLog(object sender, RoutedEventArgs e) => _controller.ViewLog();

    private void OnAttentionAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AttentionItem item })
            item.Action();
    }
}
