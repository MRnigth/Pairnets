using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Core;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Settings page's left menu in the beta looks (<see cref="DevBetaLook.On"/>): one entry per card to jump to it, with Save and
/// Cancel at the bottom so they stay in view while the page scrolls. Built in code and only then; stable keeps the page as it was.
/// </summary>
public partial class SettingsView
{
    private const double MenuWidth = 200;

    private readonly List<(Button Button, FrameworkElement Card, string Title)> _menu = [];

    /// <summary>The menu's entries that show now, in order, for the checks.</summary>
    internal IReadOnlyList<Button> MenuButtons => _menu.Where(m => m.Button.Visibility == Visibility.Visible).Select(m => m.Button).ToList();

    /// <summary>The title of the entry that is lit, or null without a menu.</summary>
    internal string? CurrentSection { get; private set; }

    private void BuildSectionMenu()
    {
        PageRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MenuWidth) });
        PageRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (UIElement child in PageRoot.Children)
            Grid.SetColumn(child, 1);

        var list = new StackPanel();
        var entries = new (string Title, FrameworkElement Card)[]
        {
            ("Your nest", NestCard), ("This computer", ComputerCard), ("Files to ignore", IgnoreExpander), ("Updates", UpdatesCard),
            ("Speed", SpeedCard), ("Where your files live", PlacementCard), ("Help", HelpCard), ("Nests", NestsCard),
            ("About", AboutCard), ("Advanced", AdvancedExpander),
        };
        foreach (var (title, card) in entries)
        {
            var button = new Button
            {
                Name = "SectionMenu" + title.Replace(" ", ""),
                Style = (Style)FindResource("PgButton"),
                Content = title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 34,
                Padding = new Thickness(14, 0, 8, 0),
                Margin = new Thickness(0, 0, 0, 2),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            button.Click += OnSectionMenu;
            card.IsVisibleChanged += (_, _) => ShowMenuEntries();
            _menu.Add((button, card, title));
            list.Children.Add(button);
        }

        // Save and Cancel leave the header and live at the bottom of the menu.
        HeaderGrid.Children.Remove(SaveButton);
        HeaderGrid.Children.Remove(CancelButton);
        SaveButton.Margin = new Thickness(0, 0, 0, 8);
        SaveButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        SaveButton.Height = 38;
        CancelButton.Margin = new Thickness(0);
        CancelButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        CancelButton.Height = 38;
        Grid.SetColumn(SaveButton, 0);
        Grid.SetColumn(CancelButton, 0);

        var actions = new StackPanel { Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        actions.Children.Add(SaveButton);
        actions.Children.Add(CancelButton);

        var dock = new DockPanel { Margin = new Thickness(28, 8, 0, 24) };
        DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(actions);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        Grid.SetColumn(dock, 0);
        Grid.SetRow(dock, 0);
        Grid.SetRowSpan(dock, 2);
        PageRoot.Children.Add(dock);

        Scroller.ScrollChanged += (_, _) => LightCurrentSection();
        Loaded += (_, _) => { ShowMenuEntries(); LightCurrentSection(); };
        ShowMenuEntries();
    }

    private void OnSectionMenu(object sender, RoutedEventArgs e)
    {
        var (_, card, title) = _menu.First(m => ReferenceEquals(m.Button, sender));
        JumpTo(title, card);
    }

    private void ShowMenuEntries()
    {
        foreach (var (button, card, _) in _menu)
            button.Visibility = card.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // A card near the end cannot reach the top of the view: the entry that was pressed stays lit until the page moves on its own.
    private string? _pinned;
    private double _pinnedOffset;

    private void JumpTo(string title, FrameworkElement card)
    {
        UpdateLayout();
        var top = card.TransformToAncestor(Form).Transform(new Point(0, 0)).Y;
        var to = Math.Clamp(top, 0, Scroller.ScrollableHeight);
        Scroller.ScrollToVerticalOffset(to);
        _pinned = title;
        _pinnedOffset = to;
        Light(title);
    }

    /// <summary>The lit entry follows the page: the card whose top is the lowest one that has reached the top of the view (two columns, so not just the last).</summary>
    private void LightCurrentSection()
    {
        if (_pinned is not null && Math.Abs(Scroller.VerticalOffset - _pinnedOffset) < 2)
            return;
        _pinned = null;
        string? current = null;
        var best = double.NegativeInfinity;
        foreach (var (button, card, title) in _menu)
        {
            if (button.Visibility != Visibility.Visible || !card.IsLoaded)
                continue;
            var top = card.TransformToAncestor(Form).Transform(new Point(0, 0)).Y;
            if (current is null || (top <= Scroller.VerticalOffset + 40 && top > best))
            {
                current = title;
                best = top;
            }
        }
        Light(current);
    }

    private void Light(string? title)
    {
        CurrentSection = title;
        foreach (var (button, _, t) in _menu)
            button.Background = t == title ? (Brush)FindResource("T.AccentSoft") : Brushes.Transparent;
    }
}
