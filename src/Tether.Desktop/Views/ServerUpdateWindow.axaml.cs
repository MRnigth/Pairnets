using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tether.Core;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

/// <summary>"Your server should be updated": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly ClientSession? _session;

    public ServerUpdateWindow()
        : this(null, "1.0.52")
    {
    }

    public ServerUpdateWindow(ClientSession? session, string serverVersion, string? appVersion = null)
    {
        InitializeComponent();
        _session = session;
        ServerVersion = serverVersion;
        appVersion ??= TetherInfo.ProductVersion;
        if (serverVersion.Length == 0)
        {
            // A server too old to report its version (installed before it could say).
            Explanation.Text = $"Your server is too old to say which version it runs; this app is version {appVersion}. Updating is recommended so both sides have the latest fixes.";
            VersionsText.Text = $"old version → {appVersion}";
        }
        else if (UpdateChecker.ServerIsOlder(serverVersion, appVersion))
        {
            Explanation.Text = $"This app is version {appVersion}, but your server runs {serverVersion}. Updating is recommended so both sides have the latest fixes.";
            VersionsText.Text = $"{serverVersion} → {appVersion}";
        }
        else
        {
            // Opened by hand from the main window: nothing is older, but a newer release may exist.
            Heading.Text = "Your server is up to date";
            Explanation.Text = $"Your server runs {serverVersion}, the same as this app ({appVersion}). \"Check for update\" installs a newer release if there is one.";
            VersionsText.Text = serverVersion;
            SkipButton.IsVisible = false;
            UpdateButton.Content = "Check for update";
        }
    }

    public string ServerVersion { get; }

    // Exposed for the headless UI test.
    internal bool Bobbing => BadgeHost.Classes.Contains("bob");
    internal bool Spinning => Ring.IsVisible && Ring.Classes.Contains("spin");
    internal string HeadingText => Heading.Text ?? string.Empty;
    internal bool CommandShown => CommandPanel.IsVisible;
    internal bool RetryShown => RetryButton.IsVisible;

    /// <summary>Set when the user chose "Don't ask for this version".</summary>
    public bool Skipped { get; private set; }

    /// <summary>Set when the user ticked "From now on, update the server automatically".</summary>
    public bool AlwaysUpdate => AlwaysBox.IsChecked == true;

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        Skipped = true;
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;
        ShowBusy("Asking the server to update");
        var result = await _session.UpdateServerAsync(new Progress<string>(ShowBusy), CancellationToken.None);
        ShowResult(result);
    }

    internal void ShowBusy(string step)
    {
        AskPanel.IsVisible = false;
        ResultPanel.IsVisible = false;
        BusyPanel.IsVisible = true;
        Ring.IsVisible = true;
        BadgeHost.Classes.Remove("bob");
        Badge.Fill = Visuals.Resource<IBrush>("S.Blue");
        BadgeIcon.Data = Visuals.Resource<Geometry>("I.Server");
        Heading.Text = "Updating your server…";
        Explanation.Text = "It downloads the newest release, checks it, installs it and restarts.";
        StepText.Text = step;
    }

    internal void ShowResult(ServerUpdateResult result)
    {
        AskPanel.IsVisible = false;
        BusyPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        Ring.IsVisible = false;
        BadgeHost.Classes.Remove("bob");
        RetryButton.IsVisible = false;
        CommandPanel.IsVisible = false;
        string brush, icon;
        if (result.Success)
        {
            (brush, icon) = ("S.Green", "I.Check");
            Heading.Text = $"Server updated to {result.ServerVersion}";
            Explanation.Text = "Syncing has resumed.";
        }
        else if (!result.CanUpdateItself)
        {
            // An older server: it has no updater yet, so it needs the install command once.
            (brush, icon) = ("S.Blue", "I.Server");
            Heading.Text = "One-time setup";
            Explanation.Text = "Your server was installed before it could update itself. Run this once on the server; from then on every update is one click here.";
            CommandPanel.IsVisible = true;
        }
        else
        {
            (brush, icon) = ("S.Orange", "I.Info");
            Heading.Text = "Not updated yet";
            Explanation.Text = result.Message + " Nothing on the server was changed.";
            RetryButton.IsVisible = true;
        }
        Badge.Fill = Visuals.Resource<IBrush>(brush);
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
        BadgeHost.Classes.Remove("pop");
        Dispatcher.UIThread.Post(() => BadgeHost.Classes.Add("pop"), DispatcherPriority.Background);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(CommandText.Text ?? string.Empty);
    }
}
