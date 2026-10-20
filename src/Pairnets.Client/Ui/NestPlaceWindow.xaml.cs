using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Where your nest keeps files" (Settings > Your nest > Change…): the nest's drives, then "Move your nest's files to …?",
/// then how the move goes. What it shows comes from <see cref="NestPlaceFlow"/>; it asks the nest every second while open.
/// </summary>
public partial class NestPlaceWindow : Window
{
    private readonly Func<ClientSession?> _session;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _asking;

    /// <param name="session">The current session, looked up on every ask: saving Settings replaces it. Null: no asking (screenshots).</param>
    /// <param name="first">What the nest said last (the Settings row's answer), shown at once.</param>
    public NestPlaceWindow(Func<ClientSession?>? session, NestStorage? first = null, TimeProvider? clock = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _session = session ?? (() => null);
        Flow = new NestPlaceFlow(clock);
        if (first is not null)
            Flow.Seen(first);
        Render();
        if (session is not null)
        {
            _poll.Tick += async (_, _) => await PollAsync();
            Loaded += async (_, _) =>
            {
                _poll.Start();
                await PollAsync();
            };
        }
        Closed += (_, _) =>
        {
            _poll.Stop();
            Motion.Spin(Ring, false);
        };
    }

    /// <summary>What the window shows (the screenshot tool and tests drive it directly).</summary>
    public NestPlaceFlow Flow { get; }

    /// <summary>One ask of the nest; a failed one is the nest switching over (or away).</summary>
    private async Task PollAsync()
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        Flow.Back();
        Render();
    }

    /// <summary>"Try again": back to the list, with the nest's newest answer.</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        Flow.Retry();
        Render();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder } && Flow.Choose(folder))
            Render();
    }

    private async void OnMove(object sender, RoutedEventArgs e) => await MoveAsync();

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

    private void OnCopy(object sender, RoutedEventArgs e) => Dialogs.CopyText(CommandText.Text);

    private enum Tone { Accent, Ok, Warn }

    private NestPlaceStep? _shown;

    /// <summary>Draws the flow's step.</summary>
    public void Render()
    {
        var step = Flow.Step;
        Heading.Text = Flow.Heading;
        Explanation.Text = Flow.Explanation;
        ListPanel.Visibility = step is NestPlaceStep.Loading or NestPlaceStep.Choose ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = step == NestPlaceStep.Confirm ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = step == NestPlaceStep.Moving ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = step is NestPlaceStep.Done or NestPlaceStep.Failed ? Visibility.Visible : Visibility.Collapsed;
        var busy = step is NestPlaceStep.Loading or NestPlaceStep.Moving;
        Ring.Visibility = RingTrack.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Motion.Spin(Ring, busy);
        switch (step)
        {
            case NestPlaceStep.Loading or NestPlaceStep.Choose:
                SetTone(Tone.Accent, "I.Disk");
                DrawDrives();
                break;
            case NestPlaceStep.Confirm:
                SetTone(Tone.Accent, "I.Disk");
                var storage = Flow.Storage!;
                var rows = Flow.Rows();
                var from = rows.FirstOrDefault(r => r.Current);
                var to = rows.FirstOrDefault(r => r.Folder == Flow.Chosen);
                FromText.Text = storage.Current;
                FromSub.Text = from is null ? (storage.Free is { } free ? Format.Bytes(free) + " free" : string.Empty) : from.Label + " · " + from.Space;
                ToText.Text = Flow.Chosen;
                ToSub.Text = to is null ? string.Empty : to.Label + " · " + to.Space;
                Explanation.Text = NestPlaceFlow.ConfirmText(Flow.Chosen ?? string.Empty);
                break;
            case NestPlaceStep.Moving:
                SetTone(Tone.Accent, "I.Disk");
                ProgressText.Text = Flow.ProgressText;
                ProgressWhere.Text = Flow.Chosen ?? Flow.Storage?.Current ?? string.Empty;
                BarTrack.UpdateLayout();
                var width = BarTrack.ActualWidth > 0 ? BarTrack.ActualWidth : 426;
                Bar.Width = Math.Max(6, width * Flow.Percent / 100.0);
                break;
            case NestPlaceStep.Done:
                SetTone(Tone.Ok, "I.Check");
                OldCopyPanel.Visibility = Flow.DeleteCommand is null ? Visibility.Collapsed : Visibility.Visible;
                CommandText.Text = Flow.DeleteCommand ?? string.Empty;
                RetryButton.Visibility = Visibility.Collapsed;
                break;
            default:
                SetTone(Tone.Warn, "I.Info");
                OldCopyPanel.Visibility = Visibility.Collapsed;
                RetryButton.Visibility = Visibility.Visible;
                break;
        }
        if (_shown != step && step is NestPlaceStep.Done or NestPlaceStep.Failed)
            Motion.Pop(BadgeHost);
        _shown = step;
    }

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
                wait.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
                Drives.Children.Add(wait);
            }
            return;
        }
        foreach (var row in rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var square = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            square.SetResourceReference(Border.BackgroundProperty, row.Current ? "T.AccentSoft" : "T.Chip");
            var icon = new LineIcon { Width = 17, Height = 17, StrokeThickness = 1.9, Data = Visuals.Resource<Geometry>("I.Disk") };
            icon.SetResourceReference(LineIcon.StrokeProperty, row.Current ? "T.AccentInk" : "T.Muted");
            square.Child = icon;
            grid.Children.Add(square);

            var texts = new StackPanel();
            var name = new TextBlock { Text = row.Label, FontSize = 13.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = row.Folder, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            folder.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSoft");
            var space = new TextBlock { Text = row.Usable ? row.Space : row.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            space.SetResourceReference(TextBlock.ForegroundProperty, row.Usable ? "T.Muted" : "T.WarnText");
            texts.Children.Add(name);
            texts.Children.Add(folder);
            texts.Children.Add(space);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            if (row.Current)
            {
                var label = new TextBlock { Text = "Now here", FontSize = 11.5, FontWeight = FontWeights.Bold };
                label.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
                var chip = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 2, 9, 2), VerticalAlignment = VerticalAlignment.Top, Child = label, Margin = new Thickness(8, 0, 0, 0) };
                chip.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
                Grid.SetColumn(chip, 2);
                grid.Children.Add(chip);
            }
            else if (row.Usable)
            {
                var chevron = new LineIcon { Width = 14, Height = 14, StrokeThickness = 2.4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Data = Visuals.Resource<Geometry>("I.ChevronRight") };
                chevron.SetResourceReference(LineIcon.StrokeProperty, "T.Muted");
                Grid.SetColumn(chevron, 2);
                grid.Children.Add(chevron);
            }

            if (row.Current)
            {
                // Where the files are now: shown like the others, but not something to pick.
                var card = new Border { Child = grid, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
                card.SetResourceReference(Border.BackgroundProperty, "T.Card");
                card.SetResourceReference(Border.BorderBrushProperty, "T.AccentInk");
                System.Windows.Automation.AutomationProperties.SetName(card, $"{row.Label}, {row.Folder}, in use now");
                Drives.Children.Add(card);
                continue;
            }
            var button = new Button
            {
                Style = (Style)FindResource("DriveButton"),
                Content = grid,
                Tag = row.Folder,
                IsEnabled = row.Usable && Flow.Storage is { CanMove: true, ByHand: false },
                Margin = new Thickness(0, 0, 0, 8),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{row.Label}, {row.Folder}, {(row.Usable ? row.Space : row.Reason)}");
            button.Click += OnPick;
            Drives.Children.Add(button);
        }
    }
}
