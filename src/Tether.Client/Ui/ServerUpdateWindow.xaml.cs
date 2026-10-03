using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Tether.Client.Themes;
using Tether.Core;
using Tether.Core.Client;

namespace Tether.Client.Ui;

/// <summary>"Your server should be updated": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly ClientSession? _session;

    public ServerUpdateWindow(ClientSession? session, string serverVersion, string? appVersion = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        _session = session;
        ServerVersion = serverVersion;
        appVersion ??= TetherInfo.ProductVersion;
        if (UpdateChecker.ServerIsOlder(serverVersion, appVersion))
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
        }
        Loaded += (_, _) => Motion.Bob(BadgeHost, AskPanel.Visibility == Visibility.Visible);
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

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;
        ShowBusy("Asking the server to update");
        var result = await _session.UpdateServerAsync(new Progress<string>(ShowBusy), CancellationToken.None);
        ShowResult(result);
    }

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
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(CommandText.Text);
}
