using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Needs you page: one card for each thing only the person can decide (a computer asking to join, a conflict,
/// a name clash, the offer of this computer's own name, anything else). The card's button runs the item's action; a card
/// with a second choice ("Keep PC-1") has both buttons in a row under its words.
/// </summary>
public partial class NeedsPageView : UserControl
{
    private IReadOnlyList<AttentionItem> _items = [];

    public NeedsPageView()
    {
        InitializeComponent();
        Show([]);
    }

    /// <summary>The cards on the page, in order (for the checks).</summary>
    public IReadOnlyList<AttentionItem> Items => _items;

    public int CardCount => Cards.Children.Count;

    public string SubtitleText => Subtitle.Text;

    public bool ShowsCalmState => CalmState.Visibility == Visibility.Visible;

    public void Show(IReadOnlyList<AttentionItem> items)
    {
        _items = items.ToList();
        Subtitle.Text = items.Count switch
        {
            0 => "Nothing is waiting for you. Everything is syncing.",
            1 => "1 thing only you can decide. Everything else keeps syncing meanwhile.",
            _ => $"{items.Count} things only you can decide. Everything else keeps syncing meanwhile.",
        };
        Cards.Children.Clear();
        foreach (var item in items)
            Cards.Children.Add(Card(item));
        CalmState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AttentionItem item })
            item.Action();
    }

    private Button ActionButton(AttentionItem item, bool primary)
    {
        var button = new Button
        {
            Style = (Style)FindResource(primary ? "PgPrimary" : "PgButton"),
            Content = item.ActionLabel,
            CommandParameter = item,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16, 0, 16, 0),
        };
        button.Click += OnAction;
        return button;
    }

    private UIElement Card(AttentionItem item)
    {
        var (fill, ink, icon) = item.Kind switch
        {
            AttentionKind.Join => ("T.AccentSoft", "T.AccentInk", "I.JoinPc"),
            AttentionKind.Conflict => ("T.WarnPill", "T.WarnText", "I.Copy"),
            AttentionKind.NameClash => ("T.Chip", "T.Text", string.Empty),
            AttentionKind.Rename => ("T.AccentSoft", "T.AccentInk", "I.Rename"),
            _ => ("T.WarnPill", "T.WarnText", "I.Warning"),
        };

        UIElement tileContent = icon.Length > 0
            ? Parts.Icon(icon, 22, ink, 1.9)
            : Parts.Text("Aa", 16, ink, FontWeights.ExtraBold);
        if (tileContent is TextBlock aa)
        {
            aa.HorizontalAlignment = HorizontalAlignment.Center;
            aa.TextTrimming = TextTrimming.None;
        }
        var tile = Parts.Square(44, 14, fill, tileContent);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 16, 0);

        var body = new StackPanel();
        var join = item.Kind == AttentionKind.Join;
        var choice = !join && item.Second is not null;

        // Title and words; a computer asking to join has its time beside the title, the others a button beside it.
        var heading = Parts.Text(item.Title, 16, "T.Text", FontWeights.ExtraBold);
        heading.TextWrapping = TextWrapping.Wrap;
        heading.TextTrimming = TextTrimming.None;
        heading.VerticalAlignment = VerticalAlignment.Top;
        var detail = join
            ? $"Allow it only if you are setting up {ComputerOf(item.Title)} right now. Its screen should show this same code."
            : item.Detail;
        var text = Parts.Text(detail, 14, "T.Muted");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(0, 4, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Top;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel();
        if (join)
        {
            var title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition());
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(heading);
            if (item.At is { } at)
            {
                var time = Parts.Text(Format.Moment(at, DateTimeOffset.Now), 12, "T.TextSoft");
                time.Margin = new Thickness(12, 4, 0, 0);
                time.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(time, 1);
                title.Children.Add(time);
            }
            words.Children.Add(title);
        }
        else if (choice)
        {
            words.Children.Add(heading);
        }
        else
        {
            words.Children.Add(heading);
            var button = ActionButton(item, primary: false);
            button.Height = 34;
            button.FontSize = 13;
            button.Margin = new Thickness(16, 0, 0, 0);
            Pg.SetRadius(button, new CornerRadius(11));
            button.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(button, 1);
            head.Children.Add(button);
        }
        words.Children.Add(text);
        head.Children.Add(words);
        Grid.SetColumnSpan(words, join || choice ? 2 : 1);
        body.Children.Add(head);

        if (join)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (item.Code is { Length: > 0 } code)
            {
                var chip = new Border { Height = 44, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(14) };
                chip.SetResourceReference(Border.BackgroundProperty, "T.Chip");
                var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                line.Children.Add(Parts.Text("Code on " + ComputerOf(item.Title), 12, "T.Muted", FontWeights.SemiBold));
                var big = Parts.Text(string.Join(' ', code.Select(c => c.ToString())), 20, "T.Text", FontWeights.ExtraBold);
                big.Margin = new Thickness(14, 0, 0, 0);
                big.ToolTip = code;
                System.Windows.Automation.AutomationProperties.SetName(big, "Code " + code);
                line.Children.Add(big);
                chip.Child = line;
                row.Children.Add(chip);
            }
            var action = ActionButton(item, primary: false);
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
            body.Children.Add(row);
        }

        if (choice)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var second = new Button
            {
                Style = (Style)FindResource("PgButton"),
                Content = item.SecondLabel,
                Tag = item,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(0, 0, 8, 0),
            };
            second.Click += (_, _) => item.Second?.Invoke();
            row.Children.Add(second);
            row.Children.Add(ActionButton(item, primary: true));
            body.Children.Add(row);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(tile);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return new Border { Style = (Style)FindResource("PgCard"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 16), Child = grid, DataContext = item };
    }

    /// <summary>"PC-1" from "PC-1 (Windows) wants to join" (the computer's name the card talks about).</summary>
    private static string ComputerOf(string title)
    {
        var cut = title.IndexOf(" (", StringComparison.Ordinal);
        if (cut < 0)
            cut = title.IndexOf(" wants", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? title[..cut] : "that computer";
    }
}
