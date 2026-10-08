using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>"Your server should be updated": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly Func<bool> _debugMode;
    private bool _debug;
    private readonly System.Text.StringBuilder _details = new();

    public ServerUpdateWindow()
        : this(null, "1.0.52")
    {
    }

    /// <param name="session">The current session, looked up on every click: saving Settings replaces it.</param>
    /// <param name="debug">Whether Debug mode is on, also looked up on every click.</param>
    public ServerUpdateWindow(Func<ClientSession?>? session, string serverVersion, string? appVersion = null, Func<bool>? debug = null)
    {
        InitializeComponent();
        _session = session ?? (() => null);
        _debugMode = debug ?? (() => false);
        _debug = _debugMode();
        DetailsPanel.IsVisible = _debug;
        ServerVersion = serverVersion;
        appVersion ??= PairnetsInfo.ProductVersion;
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
            ShowBadge("S.Green", "I.Check"); // nothing is wrong: no warning colour
            BadgeHost.Classes.Remove("bob");
        }
    }

    public string ServerVersion { get; }

    // Exposed for the headless UI test.
    internal bool Bobbing => BadgeHost.Classes.Contains("bob");
    internal bool Spinning => Ring.IsVisible && Ring.Classes.Contains("spin");
    internal string HeadingText => Heading.Text ?? string.Empty;
    internal string ExplanationText => Explanation.Text ?? string.Empty;
    internal bool CommandShown => CommandPanel.IsVisible;
    internal bool RetryShown => RetryButton.IsVisible;
    internal bool DetailsShown => DetailsPanel.IsVisible;
    internal string DetailsValue => _details.ToString();

    /// <summary>The badge colour (a brush key), for the headless UI test.</summary>
    internal string BadgeKey { get; private set; } = "S.Orange";

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

    private async void OnUpdate(object? sender, RoutedEventArgs e) => await RunUpdateAsync();

    /// <summary>"Update server": runs the update with the session of this moment and shows how it went (never throws).</summary>
    internal async Task RunUpdateAsync()
    {
        _debug = _debugMode();
        DetailsPanel.IsVisible = _debug;
        if (_session() is not { } session)
        {
            ShowResult(new ServerUpdateResult(false, "Not connected to the server. Check Settings and try again.", ServerVersion, CanUpdateItself: true));
            return;
        }
        ShowBusy("Asking the server to update");
        if (_debug)
        {
            _details.Clear();
            DetailsText.Text = string.Empty;
        }
        var trace = _debug ? new Progress<string>(AppendDetail) : null;
        ServerUpdateResult result;
        try
        {
            result = await session.UpdateServerAsync(new Progress<string>(ShowBusy), CancellationToken.None, trace: trace);
        }
        catch (Exception ex)
        {
            result = new ServerUpdateResult(false, $"Unexpected error: {ex.GetType().Name}: {ex.Message}", ServerVersion, CanUpdateItself: true);
        }
        ShowResult(result);
    }

    internal void AppendDetail(string line)
    {
        _details.AppendLine(line);
        DetailsText.Text = _details.ToString();
        DetailsScroller.ScrollToEnd();
    }

    private async void OnCopyDetails(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_details.ToString());
    }

    internal void ShowBusy(string step)
    {
        AskPanel.IsVisible = false;
        ResultPanel.IsVisible = false;
        BusyPanel.IsVisible = true;
        Ring.IsVisible = true;
        RingTrack.IsVisible = true;
        BadgeHost.IsVisible = false; // the turning ring stands in for the badge
        BadgeHost.Classes.Remove("bob");
        BadgeKey = "S.Blue";
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
        RingTrack.IsVisible = false;
        BadgeHost.IsVisible = true;
        BadgeHost.Classes.Remove("bob");
        RetryButton.IsVisible = false;
        CommandPanel.IsVisible = false;
        string brush, icon;
        if (result.AlreadyUpToDate)
        {
            (brush, icon) = ("S.Green", "I.Check");
            Heading.Text = "Your server is up to date";
            Explanation.Text = $"It runs {result.ServerVersion}, the newest release.";
        }
        else if (result.Success)
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
        ShowBadge(brush, icon);
        BadgeHost.Classes.Remove("pop");
        Dispatcher.UIThread.Post(() => BadgeHost.Classes.Add("pop"), DispatcherPriority.Background);
        if (_debug && result.Details is { } details) // after the trace lines still queued for the window
            Dispatcher.UIThread.Post(() => AppendDetail("--- What the server reports ---" + Environment.NewLine + details), DispatcherPriority.Background);
    }

    /// <summary>
    /// The badge in a status colour: a soft circle with the icon in the strong colour (S.Green up to date,
    /// S.Orange needs doing, S.Blue setting up).
    /// </summary>
    private void ShowBadge(string key, string icon)
    {
        BadgeKey = key;
        var (soft, strong) = key switch
        {
            "S.Green" => ("T.OkPill", "S.Green"),
            "S.Blue" => ("T.AccentSoft", "S.Blue"),
            _ => ("T.WarnPill", "T.WarnText"),
        };
        // Up to date is an outlined ring (as on the overview); the others a soft circle.
        if (key == "S.Green")
        {
            Badge.Fill = Brushes.Transparent;
            Visuals.Bind(Badge, Avalonia.Controls.Shapes.Shape.StrokeProperty, strong);
            Badge.StrokeThickness = 3;
        }
        else
        {
            Visuals.Bind(Badge, Avalonia.Controls.Shapes.Shape.FillProperty, soft);
            Badge.StrokeThickness = 0;
        }
        Visuals.Bind(BadgeIcon, LineIcon.StrokeProperty, strong);
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(CommandText.Text ?? string.Empty);
    }
}
