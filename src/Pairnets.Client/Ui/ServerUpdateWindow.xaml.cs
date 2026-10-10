using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>"Your server should be updated": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly Func<bool> _debugMode;
    private readonly bool _upToDate;
    private bool _debug;

    /// <param name="session">The current session, looked up on every click: saving Settings replaces it.</param>
    /// <param name="debug">Whether Debug mode is on, also looked up on every click.</param>
    public ServerUpdateWindow(Func<ClientSession?>? session, string serverVersion, string? appVersion = null, Func<bool>? debug = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _session = session ?? (() => null);
        _debugMode = debug ?? (() => false);
        _debug = _debugMode();
        DetailsPanel.Visibility = _debug ? Visibility.Visible : Visibility.Collapsed;
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
            SkipButton.Visibility = Visibility.Collapsed;
            UpdateButton.Content = "Check for update";
            Badge.SetResourceReference(Shape.FillProperty, "S.Green"); // nothing is wrong: no warning colour
            BadgeIcon.Data = Visuals.Resource<Geometry>("I.Check");
            _upToDate = true;
        }
        Loaded += (_, _) => Motion.Bob(BadgeHost, !_upToDate && AskPanel.Visibility == Visibility.Visible);
        Closed += (_, _) =>
        {
            Motion.Bob(BadgeHost, false);
            Motion.Spin(Ring, false);
        };
    }

    /// <summary>Set when the user ticked "From now on, update the server automatically".</summary>
    public bool AlwaysUpdate => AlwaysBox.IsChecked == true;

    public string ServerVersion { get; }

    /// <summary>Set when the user chose "Don't ask for this version".</summary>
    public bool Skipped { get; private set; }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Skipped = true;
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private async void OnUpdate(object sender, RoutedEventArgs e) => await RunUpdateAsync();

    /// <summary>"Update server": runs the update with the session of this moment and shows how it went (never throws).</summary>
    internal async Task RunUpdateAsync()
    {
        _debug = _debugMode();
        DetailsPanel.Visibility = _debug ? Visibility.Visible : Visibility.Collapsed;
        if (_session() is not { } session)
        {
            ShowResult(new ServerUpdateResult(false, "Not connected to the server. Check Settings and try again.", ServerVersion, CanUpdateItself: true));
            return;
        }
        ShowBusy("Asking the server to update");
        if (_debug)
            DetailsText.Clear();
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

    private void AppendDetail(string line)
    {
        DetailsText.AppendText(line + Environment.NewLine);
        DetailsText.ScrollToEnd();
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e) => Dialogs.CopyText(DetailsText.Text);

    public void ShowBusy(string step)
    {
        AskPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        Ring.Visibility = Visibility.Visible;
        Motion.Bob(BadgeHost, false);
        Motion.Spin(Ring, true);
        Badge.SetResourceReference(Shape.FillProperty, "S.Blue");
        BadgeIcon.Data = Visuals.Resource<Geometry>("I.Server");
        Heading.Text = "Updating your server…";
        Explanation.Text = "It downloads the newest release, checks it, installs it and restarts.";
        StepText.Text = step;
    }

    public void ShowResult(ServerUpdateResult result)
    {
        AskPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        Motion.Spin(Ring, false);
        Motion.Bob(BadgeHost, false);
        Ring.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        CommandPanel.Visibility = Visibility.Collapsed;
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
            CommandPanel.Visibility = Visibility.Visible;
        }
        else
        {
            (brush, icon) = ("S.Orange", "I.Info");
            Heading.Text = "Not updated yet";
            Explanation.Text = result.Message + " Nothing on the server was changed.";
            RetryButton.Visibility = Visibility.Visible;
        }
        Badge.SetResourceReference(Shape.FillProperty, brush);
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
        Motion.Pop(BadgeHost);
        if (_debug && result.Details is { } details) // after the trace lines still queued for the window
            Dispatcher.InvokeAsync(() => AppendDetail("--- What the server reports ---" + Environment.NewLine + details), DispatcherPriority.Background);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Dialogs.CopyText(CommandText.Text);
}
