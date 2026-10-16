using Pairnets.Core.Api;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>"This app is newer than your nest": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly Func<bool> _debugMode;
    private readonly bool _upToDate;
    private readonly string _appVersion;
    private readonly string? _nestName;
    private bool _debug;

    /// <param name="session">The current session, looked up on every click: saving Settings replaces it.</param>
    /// <param name="debug">Whether Debug mode is on, also looked up on every click.</param>
    /// <param name="nestName">How the nest is called on the cards (default: the address of the session's nest).</param>
    public ServerUpdateWindow(Func<ClientSession?>? session, string serverVersion, string? appVersion = null, Func<bool>? debug = null, string? nestName = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _session = session ?? (() => null);
        _debugMode = debug ?? (() => false);
        _nestName = nestName;
        _debug = _debugMode();
        DetailsPanel.Visibility = _debug ? Visibility.Visible : Visibility.Collapsed;
        ServerVersion = serverVersion;
        appVersion ??= PairnetsInfo.ProductVersion;
        _appVersion = appVersion;
        AppVersion.Text = appVersion;
        NestSub.Text = NestName();
        if (serverVersion.Length == 0)
        {
            // A nest too old to report its version (installed before it could say).
            Heading.Text = "Your nest should be updated";
            Explanation.Text = $"Your nest is too old to say which version it runs; this app is version {appVersion}. Update it so both sides have the same fixes.";
            NestVersion.Text = "older";
            VersionsText.Text = $"old version → {appVersion}";
        }
        else if (UpdateChecker.ServerIsOlder(serverVersion, appVersion))
        {
            Explanation.Text = "Update the nest to match, so both sides have the same fixes.";
            NestVersion.Text = serverVersion;
            VersionsText.Text = $"{serverVersion} → {appVersion}";
        }
        else
        {
            // Opened by hand from the main window: nothing is older, but a newer release may exist.
            Heading.Text = "Your nest is up to date";
            Explanation.Text = $"Your nest runs {serverVersion}, the same as this app ({appVersion}). \"Check for update\" installs a newer release if there is one.";
            NestVersion.Text = serverVersion;
            VersionsText.Text = serverVersion;
            SkipButton.Visibility = Visibility.Collapsed;
            UpdateButton.Content = "Check for update";
            _upToDate = true;
            SetTone(Tone.Ok, "I.Check");
        }
        if (!_upToDate)
            SetTone(Tone.Accent, "I.Server");
        Loaded += (_, _) => Motion.Bob(BadgeHost, !_upToDate && AskPanel.Visibility == Visibility.Visible);
        Closed += (_, _) =>
        {
            Motion.Bob(BadgeHost, false);
            Motion.Spin(Ring, false);
        };
    }

    /// <summary>"sync.pairnets.app" or "nest.example.com": the nest's address under its version on the cards.</summary>
    public string NestNameText => NestSub.Text;

    private string NestName()
    {
        if (!string.IsNullOrEmpty(_nestName))
            return _nestName;
        return _session()?.Settings.ServerUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    /// <summary>Set when the user ticked "From now on, update the nest by itself".</summary>
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

    /// <summary>"Update the nest": runs the update with the session of this moment and shows how it went (never throws).</summary>
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
            result = new ServerUpdateResult(false, "Something went wrong while updating the server. " + FriendlyErrors.Describe(ex) + " Nothing was changed on the server.", ServerVersion, CanUpdateItself: true);
        }
        ShowResult(result);
    }

    private void AppendDetail(string line)
    {
        DetailsText.AppendText(line + Environment.NewLine);
        DetailsText.ScrollToEnd();
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e) => Dialogs.CopyText(DetailsText.Text);

    private enum Tone { Accent, Ok, Warn }

    /// <summary>Colours the round icon at the top: a soft circle and its ink, by tone.</summary>
    private void SetTone(Tone tone, string icon)
    {
        var (fill, ink) = tone switch
        {
            Tone.Ok => ("T.OkPill", "T.OkText"),
            Tone.Warn => ("T.WarnPill", "T.WarnText"),
            _ => ("T.AccentSoft", "T.AccentInk"),
        };
        Badge.SetResourceReference(Shape.FillProperty, fill);
        BadgeIcon.SetResourceReference(LineIcon.StrokeProperty, ink);
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
        UpBadge.Visibility = tone == Tone.Accent && icon == "I.Server" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly (string Title, string Active, string Next)[] StepTexts =
    [
        ("Getting the update", "Downloading and checking it…", "Downloaded and checked"),
        ("Installing it on the nest", "Putting the new version in place…", "The new version goes in"),
        ("Restarting the nest", "Starting it again…", "Then syncing carries on by itself"),
    ];

    /// <summary>Which of the three steps a progress message belongs to (0, 1 or 2).</summary>
    internal static int StepOf(string message) =>
        message.Contains("install", StringComparison.OrdinalIgnoreCase) ? 1
        : message.Contains("restart", StringComparison.OrdinalIgnoreCase) ? 2
        : 0;

    public void ShowBusy(string step)
    {
        AskPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        RingTrack.Visibility = Visibility.Visible;
        Ring.Visibility = Visibility.Visible;
        Motion.Bob(BadgeHost, false);
        Motion.Spin(Ring, true);
        SetTone(Tone.Accent, "I.Server");
        UpBadge.Visibility = Visibility.Collapsed;
        Heading.Text = "Updating your nest…";
        Explanation.Text = "It gets the newest release, checks it, installs it and restarts. Syncing pauses for about a minute.";
        StepText.Text = "You can close this window. Pairnets reconnects by itself.";
        StepWhere.Text = ServerVersion.Length == 0 ? $"to {_appVersion}" : $"{ServerVersion} to {_appVersion}";
        var current = StepOf(step);
        StepCount.Text = $"Step {current + 1} of 3";
        StepBar.Width = (current + 0.5) / 3 * 410;
        BuildSteps(current);
    }

    private void BuildSteps(int current)
    {
        Steps.Children.Clear();
        for (var i = 0; i < StepTexts.Length; i++)
        {
            var (title, active, next) = StepTexts[i];
            var done = i < current;
            var now = i == current;
            var row = new Grid { Margin = new Thickness(0, 0, 0, i == StepTexts.Length - 1 ? 0 : 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            if (done)
            {
                dot.SetResourceReference(Border.BackgroundProperty, "T.OkPill");
                var tick = new LineIcon { Width = 13, Height = 13, StrokeThickness = 3.2, Data = Visuals.Resource<Geometry>("I.Check") };
                tick.SetResourceReference(LineIcon.StrokeProperty, "T.OkText");
                dot.Child = tick;
            }
            else if (now)
            {
                dot.BorderThickness = new Thickness(2);
                dot.SetResourceReference(Border.BorderBrushProperty, "T.Accent");
                dot.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
                var inner = new Ellipse { Width = 8, Height = 8 };
                inner.SetResourceReference(Shape.FillProperty, "T.Accent");
                dot.Child = inner;
            }
            else
            {
                dot.BorderThickness = new Thickness(2);
                dot.SetResourceReference(Border.BorderBrushProperty, "T.Track");
            }
            row.Children.Add(dot);
            var text = new StackPanel { Margin = new Thickness(0, 1, 0, 0) };
            var heading = new TextBlock { Text = title, FontSize = 13.5, FontWeight = now ? FontWeights.ExtraBold : FontWeights.Bold };
            heading.SetResourceReference(TextBlock.ForegroundProperty, i > current ? "T.Muted" : "T.Text");
            var sub = new TextBlock { Text = done ? "Done" : now ? active : next, FontSize = 12, FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal };
            sub.SetResourceReference(TextBlock.ForegroundProperty, now ? "T.AccentInk" : "T.TextSoft");
            text.Children.Add(heading);
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (now)
            {
                var label = new TextBlock { Text = "Now", FontSize = 11.5, FontWeight = FontWeights.Bold };
                label.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
                var chip = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 2, 9, 2), VerticalAlignment = VerticalAlignment.Top, Child = label };
                chip.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }
            Steps.Children.Add(row);
        }
    }

    public void ShowResult(ServerUpdateResult result)
    {
        AskPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        Motion.Spin(Ring, false);
        Motion.Bob(BadgeHost, false);
        Ring.Visibility = Visibility.Collapsed;
        RingTrack.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        CommandPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        Tone tone;
        string icon;
        if (result.AlreadyUpToDate)
        {
            (tone, icon) = (Tone.Ok, "I.Check");
            Heading.Text = "Your nest is up to date";
            Explanation.Text = $"It runs {result.ServerVersion}, the newest release.";
        }
        else if (result.Success)
        {
            (tone, icon) = (Tone.Ok, "I.Check");
            Heading.Text = $"Your nest is on {result.ServerVersion}";
            Explanation.Text = "Syncing has resumed. The nest and this app now match.";
            DoneNestVersion.Text = result.ServerVersion;
            DoneNestSub.Text = NestName();
            DoneAppVersion.Text = _appVersion;
            DoneAppSub.Text = "This computer";
            DonePanel.Visibility = Visibility.Visible;
        }
        else if (!result.CanUpdateItself)
        {
            // An older nest: it has no updater yet, so it needs the install command once.
            (tone, icon) = (Tone.Accent, "I.Server");
            Heading.Text = "One-time setup";
            Explanation.Text = "Your nest was installed before it could update itself. Run this once on the nest; from then on every update is one click here.";
            CommandPanel.Visibility = Visibility.Visible;
        }
        else
        {
            (tone, icon) = (Tone.Warn, "I.Info");
            Heading.Text = "Not updated yet";
            Explanation.Text = result.Message + " Nothing on the nest was changed.";
            RetryButton.Visibility = Visibility.Visible;
        }
        SetTone(tone, icon);
        UpBadge.Visibility = Visibility.Collapsed;
        Motion.Pop(BadgeHost);
        if (_debug && result.Details is { } details) // after the trace lines still queued for the window
            Dispatcher.InvokeAsync(() => AppendDetail("--- What the server reports ---" + Environment.NewLine + details), DispatcherPriority.Background);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Dialogs.CopyText(CommandText.Text);
}
