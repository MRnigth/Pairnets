using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// "Where your nest keeps files" (Settings > Your nest > Change…): the nest's drives, then "Move your nest's files to …?",
/// then how the move goes. What it shows comes from <see cref="NestPlaceFlow"/>; it asks the nest every second while open.
/// </summary>
public partial class NestPlaceWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _asking;
    private NestPlaceStep? _shown;

    public NestPlaceWindow()
        : this(null)
    {
    }

    /// <param name="session">The current session, looked up on every ask: saving Settings replaces it. Null: no asking (screenshots).</param>
    /// <param name="first">What the nest said last (the Settings row's answer), shown at once.</param>
    public NestPlaceWindow(Func<ClientSession?>? session, NestStorage? first = null, TimeProvider? clock = null)
    {
        InitializeComponent();
        _session = session ?? (() => null);
        Flow = new NestPlaceFlow(clock);
        if (first is not null)
            Flow.Seen(first);
        Render();
        if (session is not null)
        {
            _poll.Tick += async (_, _) => await PollAsync();
            Opened += async (_, _) =>
            {
                _poll.Start();
                await PollAsync();
            };
        }
        Closed += (_, _) => _poll.Stop();
    }

    /// <summary>What the window shows (tests and the screenshots drive it directly).</summary>
    internal NestPlaceFlow Flow { get; }

    // Exposed for the headless UI test.
    internal string HeadingText => Heading.Text ?? string.Empty;
    internal string ExplanationText => Explanation.Text ?? string.Empty;
    internal string ProgressLine => ProgressText.Text ?? string.Empty;
    internal string CommandLine => CommandText.Text ?? string.Empty;
    internal IReadOnlyList<Button> DriveButtons => Drives.Children.OfType<Button>().ToList();
    internal bool ConfirmShown => ConfirmPanel.IsVisible;
    internal bool ListShown => ListPanel.IsVisible;

    /// <summary>One ask of the nest; a failed one is the nest switching over (or away).</summary>
    internal async Task PollAsync()
    {
        if (_asking || _session() is not { } session)
            return;
        _asking = true;
        try
        {
            var storage = await session.Api.GetStorageAsync(CancellationToken.None);
            Flow.Seen(storage ?? new NestStorage(false, string.Empty, string.Empty, false, false, null, null, []));
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException or PairnetsAuthException or HttpRequestException or TaskCanceledException)
        {
            Flow.Unreachable();
        }
        finally
        {
            _asking = false;
        }
        Render();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        Flow.Back();
        Render();
    }

    /// <summary>"Try again": back to the list, with the nest's newest answer.</summary>
    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        Flow.Retry();
        Render();
    }

    private void OnPick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder } && Flow.Choose(folder))
            Render();
    }

    private async void OnMove(object? sender, RoutedEventArgs e) => await MoveAsync();

    /// <summary>"Move the files": asks the nest, then follows the move (never throws).</summary>
    internal async Task MoveAsync()
    {
        if (Flow.Chosen is not { } folder)
            return;
        if (_session() is not { } session)
        {
            Flow.AskFailed("Not connected to your nest. Check Settings and try again.");
            Render();
            return;
        }
        Flow.Asking();
        Render();
        try
        {
            Flow.Answered(await session.Api.RequestStorageMoveAsync(folder, CancellationToken.None));
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException or PairnetsAuthException or HttpRequestException or TaskCanceledException)
        {
            Flow.AskFailed("Pairnets could not ask your nest. " + FriendlyErrors.Describe(ex) + " Nothing was changed.");
        }
        Render();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(CommandText.Text ?? string.Empty);
    }

    /// <summary>Draws the flow's step.</summary>
    internal void Render()
    {
        var step = Flow.Step;
        Heading.Text = Flow.Heading;
        Explanation.Text = Flow.Explanation;
        ListPanel.IsVisible = step is NestPlaceStep.Loading or NestPlaceStep.Choose;
        ConfirmPanel.IsVisible = step == NestPlaceStep.Confirm;
        BusyPanel.IsVisible = step == NestPlaceStep.Moving;
        ResultPanel.IsVisible = step is NestPlaceStep.Done or NestPlaceStep.Failed;
        var busy = step is NestPlaceStep.Loading or NestPlaceStep.Moving;
        Ring.IsVisible = busy;
        RingTrack.IsVisible = busy;
        Badge.Width = Badge.Height = busy ? 36 : 48;
        Badge.CornerRadius = new CornerRadius(busy ? 18 : 24);
        BadgeIcon.Width = BadgeIcon.Height = busy ? 18 : 22;
        switch (step)
        {
            case NestPlaceStep.Loading or NestPlaceStep.Choose:
                SetBadge(PictureTone.Accent, "I.Disk");
                DrawDrives();
                break;
            case NestPlaceStep.Confirm:
                SetBadge(PictureTone.Accent, "I.Disk");
                var storage = Flow.Storage!;
                var rows = Flow.Rows();
                var from = rows.FirstOrDefault(r => r.Current);
                var to = rows.FirstOrDefault(r => r.Folder == Flow.Chosen);
                FromText.Text = storage.Current;
                FromSub.Text = from is null ? (storage.Free is { } free ? Format.Bytes(free) + " free" : string.Empty) : from.Label + " · " + from.Space;
                ToText.Text = Flow.Chosen;
                ToSub.Text = to is null ? string.Empty : to.Label + " · " + to.Space;
                break;
            case NestPlaceStep.Moving:
                SetBadge(PictureTone.Accent, "I.Disk");
                ProgressText.Text = Flow.ProgressText;
                ProgressWhere.Text = Flow.Chosen ?? Flow.Storage?.Current ?? string.Empty;
                var percent = Math.Clamp(Flow.Percent, 2, 100);
                BarGrid.ColumnDefinitions = new ColumnDefinitions($"{percent}*,{100 - percent}*");
                break;
            case NestPlaceStep.Done:
                SetBadge(PictureTone.Ok, "I.Check");
                OldCopyPanel.IsVisible = Flow.DeleteCommand is not null;
                CommandText.Text = Flow.DeleteCommand ?? string.Empty;
                RetryButton.IsVisible = false;
                CloseButton.Classes.Set("accent", true);
                CloseButton.Classes.Set("SecondaryButton", false);
                break;
            default:
                SetBadge(PictureTone.Warn, "I.Info");
                OldCopyPanel.IsVisible = false;
                RetryButton.IsVisible = true;
                CloseButton.Classes.Set("accent", false);
                CloseButton.Classes.Set("SecondaryButton", true);
                break;
        }
        if (_shown != step && step is NestPlaceStep.Done or NestPlaceStep.Failed)
            Motion.Replay(BadgeHost, "pop");
        _shown = step;
    }

    /// <summary>The badge's tone as a brush key (blue: busy or asking, green: done, orange: not moved), for the headless UI test.</summary>
    internal string BadgeKey { get; private set; } = "S.Blue";

    private void SetBadge(PictureTone tone, string icon)
    {
        var (halo, ink, _) = SignInPicture.Colours(tone);
        Badge.Bind(Border.BackgroundProperty, Badge.GetResourceObservable(halo));
        BadgeIcon.Bind(LineIcon.StrokeProperty, BadgeIcon.GetResourceObservable(ink));
        BadgeIcon.Data = Visuals.Resource<Geometry>(icon);
        BadgeIcon.StrokeThickness = icon == "I.Check" ? 2.6 : 1.8;
        BadgeKey = tone switch { PictureTone.Ok => "S.Green", PictureTone.Warn => "S.Orange", _ => "S.Blue" };
    }

    private static void Res(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is StyledElement element and IResourceHost host)
            element.Bind(property, host.GetResourceObservable(key));
    }

    /// <summary>The drives: name, folder, space; the one in use marked, the ones that can't be used greyed with the reason.</summary>
    private void DrawDrives()
    {
        Drives.Children.Clear();
        var rows = Flow.Rows();
        if (rows.Count == 0)
        {
            if (Flow.Step == NestPlaceStep.Loading)
            {
                var wait = new TextBlock { Text = "Asking your nest for its drives…", FontSize = 13, Margin = new Thickness(2, 0, 0, 0) };
                wait.Classes.Add("muted");
                Drives.Children.Add(wait);
            }
            return;
        }
        var movable = Flow.Storage is { CanMove: true, ByHand: false };
        foreach (var row in rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var square = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            Res(square, Border.BackgroundProperty, row.Current ? "T.AccentSoft" : "T.Chip");
            var icon = new LineIcon { Width = 17, Height = 17, StrokeThickness = 1.9, Data = Visuals.Resource<Geometry>("I.Disk"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Res(icon, LineIcon.StrokeProperty, row.Current ? "T.AccentInk" : "T.Muted");
            square.Child = icon;
            grid.Children.Add(square);

            var texts = new StackPanel();
            var name = new TextBlock { Text = row.Label, FontSize = 13.5, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = row.Folder, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace") };
            Res(folder, TextBlock.ForegroundProperty, "T.TextSoft");
            var space = new TextBlock { Text = row.Usable ? row.Space : row.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            Res(space, TextBlock.ForegroundProperty, row.Usable ? "T.Muted" : "T.WarnText");
            texts.Children.Add(name);
            texts.Children.Add(folder);
            texts.Children.Add(space);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            if (row.Current)
            {
                var label = new TextBlock { Text = "Now here", FontSize = 11.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
                Res(label, TextBlock.ForegroundProperty, "T.AccentInk");
                var pill = new Border { Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0), Child = label };
                Res(pill, Border.BackgroundProperty, "T.AccentSoft");
                Grid.SetColumn(pill, 2);
                grid.Children.Add(pill);
            }
            else if (row.Usable && movable)
            {
                var chevron = new LineIcon { Width = 14, Height = 14, StrokeThickness = 2.4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Data = Visuals.Resource<Geometry>("I.ChevronRight") };
                Res(chevron, LineIcon.StrokeProperty, "T.Muted");
                Grid.SetColumn(chevron, 2);
                grid.Children.Add(chevron);
            }

            if (row.Current)
            {
                // Where the files are now: shown like the others, but not something to pick.
                var card = new Border { Child = grid, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12), BorderThickness = new Thickness(1) };
                Res(card, Border.BackgroundProperty, "T.Card");
                Res(card, Border.BorderBrushProperty, "T.BannerBorder");
                Avalonia.Automation.AutomationProperties.SetName(card, $"{row.Label}, {row.Folder}, in use now");
                Drives.Children.Add(card);
                continue;
            }
            var button = new Button
            {
                Content = grid,
                Tag = row.Folder,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 12),
                IsEnabled = row.Usable && movable,
                Opacity = row.Usable ? 1 : 0.6,
            };
            button.Classes.Add("rowbtn");
            button.Classes.Add("cardbtn");
            Avalonia.Automation.AutomationProperties.SetName(button, $"{row.Label}, {row.Folder}, {(row.Usable ? row.Space : row.Reason)}");
            button.Click += OnPick;
            Drives.Children.Add(button);
        }
    }
}
