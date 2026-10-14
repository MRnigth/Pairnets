using Avalonia;
using Pairnets.Core.Api;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>"Update your nest": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly Func<bool> _debugMode;
    private readonly string _appVersion;
    private readonly string? _nestName;
    private readonly string? _computerName;
    private bool _debug;
    private readonly System.Text.StringBuilder _details = new();

    public ServerUpdateWindow()
        : this(null, "1.0.52")
    {
    }

    /// <param name="session">The current session, looked up on every click: saving Settings replaces it.</param>
    /// <param name="debug">Whether Debug mode is on, also looked up on every click.</param>
    /// <param name="nestName">How the nest is called on the cards (default: the address of the session's nest).</param>
    public ServerUpdateWindow(Func<ClientSession?>? session, string serverVersion, string? appVersion = null, Func<bool>? debug = null, string? nestName = null, string? computerName = null)
    {
        InitializeComponent();
        _session = session ?? (() => null);
        _debugMode = debug ?? (() => false);
        _nestName = nestName;
        _computerName = computerName;
        _debug = _debugMode();
        DetailsPanel.IsVisible = _debug;
        ServerVersion = serverVersion;
        appVersion ??= PairnetsInfo.ProductVersion;
        _appVersion = appVersion;
        AppVersionText.Text = appVersion;
        NestNameText.Text = NestName();
        if (serverVersion.Length == 0)
        {
            // A nest too old to report its version (installed before it could say).
            Heading.Text = "Your nest should be updated";
            Explanation.Text = $"Your nest is too old to say which version it runs; this app is version {appVersion}. Updating is recommended so both sides have the latest fixes.";
            NestVersionText.Text = "older";
            SetBadge(PictureTone.Accent, "I.Nest", "S.Orange", up: true, bob: true);
        }
        else if (UpdateChecker.ServerIsOlder(serverVersion, appVersion))
        {
            Heading.Text = "This app is newer than your nest";
            Explanation.Text = "Update the nest to match, so both sides have the same fixes.";
            NestVersionText.Text = serverVersion;
            SetBadge(PictureTone.Accent, "I.Nest", "S.Orange", up: true, bob: true);
        }
        else
        {
            // Opened by hand from the main window: nothing is older, but a newer release may exist.
            Heading.Text = "Your nest is up to date";
            Explanation.Text = $"Your nest runs {serverVersion}, the same as this app ({appVersion}). \"Check for update\" installs a newer release if there is one.";
            NestVersionText.Text = serverVersion;
            NestCardLabel.Text = "Your nest";
            AppNoteText.Text = "Same version";
            SkipButton.IsVisible = false;
            UpdateButtonText.Text = "Check for update";
            Arrow.Background = Visuals.Resource<IBrush>("T.OkPill");
            ArrowIcon.Stroke = Visuals.Resource<IBrush>("T.OkText");
            ArrowIcon.Data = Visuals.Resource<Geometry>("I.Check");
            SetBadge(PictureTone.Ok, "I.Check", "S.Green", up: false, bob: false); // nothing is wrong: no warning colour
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

    /// <summary>The badge's tone as a brush key (orange: needs an answer, blue: busy, green: fine), for the headless UI test.</summary>
    internal string BadgeKey { get; private set; } = "S.Orange";

    /// <summary>Set when the user chose "Don't ask for this version".</summary>
    public bool Skipped { get; private set; }

    /// <summary>Set when the user ticked "From now on, update the nest automatically".</summary>
    public bool AlwaysUpdate => AlwaysBox.IsChecked == true;

    /// <summary>The round icon at the top: its soft tone, its glyph, the small "up" badge, and whether it bobs.</summary>
    private void SetBadge(PictureTone tone, string icon, string key, bool up, bool bob)
    {
        var (halo, ink, _) = SignInPicture.Colours(tone);
        Badge.Bind(Border.BackgroundProperty, Badge.GetResourceObservable(halo));
        BadgeIcon.Bind(LineIcon.StrokeProperty, BadgeIcon.GetResourceObservable(ink));
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
        BadgeIcon.StrokeThickness = icon == "I.Check" ? 2.6 : 1.8;
        BadgeUp.IsVisible = up;
        BadgeHost.Classes.Set("bob", bob);
        BadgeKey = key;
    }

    private string NestName()
    {
        if (!string.IsNullOrEmpty(_nestName))
            return _nestName;
        return _session()?.Settings.ServerUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private string ThisComputer() => _computerName ?? _session()?.Settings.DeviceName ?? Environment.MachineName;

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        Skipped = true;
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();

    private async void OnUpdate(object? sender, RoutedEventArgs e) => await RunUpdateAsync();

    /// <summary>"Update the nest": runs the update with the session of this moment and shows how it went (never throws).</summary>
    internal async Task RunUpdateAsync()
    {
        _debug = _debugMode();
        DetailsPanel.IsVisible = _debug;
        if (_session() is not { } session)
        {
            ShowResult(new ServerUpdateResult(false, "Not connected to your nest. Check Settings and try again.", ServerVersion, CanUpdateItself: true));
            return;
        }
        ShowBusy("Asking the nest to update");
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
            result = new ServerUpdateResult(false, "Something went wrong while updating the server. " + FriendlyErrors.Describe(ex) + " Nothing was changed on the server.", ServerVersion, CanUpdateItself: true);
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

    /// <summary>Which of the three steps a progress message belongs to: getting it, installing it, restarting.</summary>
    internal static int StepOf(string message) =>
        message.Contains("install", StringComparison.OrdinalIgnoreCase) ? 2
        : message.Contains("restart", StringComparison.OrdinalIgnoreCase) ? 3
        : 1;

    internal void ShowBusy(string step)
    {
        AskPanel.IsVisible = false;
        ResultPanel.IsVisible = false;
        BusyPanel.IsVisible = true;
        Ring.IsVisible = true;
        RingTrack.IsVisible = true;
        Badge.Width = Badge.Height = 36;
        Badge.CornerRadius = new CornerRadius(18);
        SetBadge(PictureTone.Accent, "I.Nest", "S.Blue", up: false, bob: false);
        BadgeIcon.Width = BadgeIcon.Height = 18;
        Heading.Text = "Updating your nest…";
        Explanation.Text = "It gets the newest release, checks it, installs it and restarts. Syncing pauses for about a minute.";
        var n = StepOf(step);
        StepText.Text = $"Step {n} of 3";
        var nest = NestName();
        StepWhere.Text = (nest.Length > 0 ? nest + " · " : string.Empty) + (ServerVersion.Length > 0 ? $"{ServerVersion} to {_appVersion}" : $"to {_appVersion}");
        BarGrid.ColumnDefinitions = new ColumnDefinitions(n switch { 1 => "20*,80*", 2 => "55*,45*", _ => "85*,15*" });
        DrawSteps(n);
    }

    private static void Res(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is StyledElement element and IResourceHost host)
            element.Bind(property, host.GetResourceObservable(key));
    }

    /// <summary>The three steps with their markers: done (green check), now (blue ring) and next (grey ring).</summary>
    private void DrawSteps(int current)
    {
        StepsHost.Children.Clear();
        (string Title, string Done, string Now, string Next)[] steps =
        [
            ("Getting the update", "Downloaded and checked", "Getting the newest release…", "Downloads and checks it"),
            ("Installing it on the nest", "Installed", $"Putting {_appVersion} in place…", "Puts the new version in place"),
            ("Restarting the nest", "Restarted", "Restarting…", "Then syncing carries on by itself"),
        ];
        for (var i = 0; i < steps.Length; i++)
        {
            var state = i + 1 < current ? 0 : i + 1 == current ? 1 : 2; // 0 done, 1 now, 2 next
            var last = i == steps.Length - 1;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
            var rail = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Border marker = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(12) };
            switch (state)
            {
                case 0:
                    Res(marker, Border.BackgroundProperty, "T.OkPill");
                    var check = new LineIcon { Width = 13, Height = 13, StrokeThickness = 3.2, Data = Visuals.Resource<Geometry>("I.Check") };
                    Res(check, LineIcon.StrokeProperty, "T.OkText");
                    marker.Child = check;
                    break;
                case 1:
                    marker.BorderThickness = new Thickness(2);
                    Res(marker, Border.BorderBrushProperty, "T.Accent");
                    Res(marker, Border.BackgroundProperty, "T.AccentSoft");
                    var dot = new Ellipse { Width = 8, Height = 8 };
                    Res(dot, Shape.FillProperty, "T.Accent");
                    marker.Child = dot;
                    break;
                default:
                    marker.BorderThickness = new Thickness(2);
                    Res(marker, Border.BorderBrushProperty, "T.Track");
                    break;
            }
            rail.Children.Add(marker);
            if (!last)
            {
                var line = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 4), HorizontalAlignment = HorizontalAlignment.Center };
                Res(line, Border.BackgroundProperty, state == 0 ? "T.OkLine" : "T.Track");
                Grid.SetRow(line, 1);
                rail.Children.Add(line);
            }
            row.Children.Add(rail);

            var texts = new StackPanel { Margin = new Thickness(12, 1, 0, last ? 0 : 10) };
            var title = new TextBlock { Text = steps[i].Title, FontSize = 13.5, FontWeight = state == 1 ? FontWeight.ExtraBold : FontWeight.Bold };
            if (state == 2)
                Res(title, TextBlock.ForegroundProperty, "T.Muted");
            var sub = new TextBlock { Text = state == 0 ? steps[i].Done : state == 1 ? steps[i].Now : steps[i].Next, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Res(sub, TextBlock.ForegroundProperty, state == 1 ? "T.AccentInk" : "T.TextSoft");
            if (state == 1)
                sub.FontWeight = FontWeight.SemiBold;
            texts.Children.Add(title);
            texts.Children.Add(sub);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);

            if (state != 2)
            {
                Control tag;
                if (state == 0)
                {
                    var done = new TextBlock { Text = "Done", FontSize = 12, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 3, 0, 0) };
                    Res(done, TextBlock.ForegroundProperty, "T.OkText");
                    tag = done;
                }
                else
                {
                    var now = new TextBlock { Text = "Now", FontSize = 11.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
                    Res(now, TextBlock.ForegroundProperty, "T.AccentInk");
                    var pill = new Border { Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 0), VerticalAlignment = VerticalAlignment.Top, Child = now };
                    Res(pill, Border.BackgroundProperty, "T.AccentSoft");
                    tag = pill;
                }
                Grid.SetColumn(tag, 2);
                row.Children.Add(tag);
            }
            StepsHost.Children.Add(row);
        }
    }

    internal void ShowResult(ServerUpdateResult result)
    {
        AskPanel.IsVisible = false;
        BusyPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        Ring.IsVisible = false;
        RingTrack.IsVisible = false;
        Badge.Width = Badge.Height = 48;
        Badge.CornerRadius = new CornerRadius(24);
        BadgeIcon.Width = BadgeIcon.Height = 22;
        RetryButton.IsVisible = false;
        CommandPanel.IsVisible = false;
        DonePanel.IsVisible = false;
        CloseButton.Classes.Set("accent", true);
        CloseButton.Classes.Set("SecondaryButton", false);
        PictureTone tone;
        string key, icon;
        if (result.AlreadyUpToDate || result.Success)
        {
            (tone, key, icon) = (PictureTone.Ok, "S.Green", "I.Check");
            var version = result.ServerVersion ?? ServerVersion;
            Heading.Text = result.AlreadyUpToDate ? "Your nest is up to date" : $"Your nest is on {version}";
            Explanation.Text = result.AlreadyUpToDate ? $"It runs {version}, the newest release." : "Syncing has resumed. The nest and this app now match.";
            DonePanel.IsVisible = true;
            DoneNestVersion.Text = version;
            DoneNestName.Text = NestName();
            DoneAppVersion.Text = _appVersion;
            DoneAppName.Text = ThisComputer();
            DoneSince.Text = $"since {DateTime.Now:HH:mm}";
        }
        else if (!result.CanUpdateItself)
        {
            // An older nest: it has no updater yet, so it needs the install command once.
            (tone, key, icon) = (PictureTone.Accent, "S.Blue", "I.Terminal");
            Heading.Text = "One-time setup";
            Explanation.Text = "Your nest was installed before it could update itself. Run this once on the nest; from then on every update is one click here.";
            CommandPanel.IsVisible = true;
            CloseButton.Classes.Set("accent", false);
            CloseButton.Classes.Set("SecondaryButton", true);
        }
        else
        {
            (tone, key, icon) = (PictureTone.Warn, "S.Orange", "I.Info");
            Heading.Text = "Not updated yet";
            Explanation.Text = result.Message + " Nothing on the nest was changed.";
            RetryButton.IsVisible = true;
            CloseButton.Classes.Set("accent", false);
            CloseButton.Classes.Set("SecondaryButton", true);
        }
        SetBadge(tone, icon, key, up: false, bob: false);
        BadgeHost.Classes.Remove("pop");
        Dispatcher.UIThread.Post(() => BadgeHost.Classes.Add("pop"), DispatcherPriority.Background);
        if (_debug && result.Details is { } details) // after the trace lines still queued for the window
            Dispatcher.UIThread.Post(() => AppendDetail("--- What the server reports ---" + Environment.NewLine + details), DispatcherPriority.Background);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(CommandText.Text ?? string.Empty);
    }
}
