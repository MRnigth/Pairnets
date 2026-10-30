using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The Settings page's left menu in the beta looks (<see cref="DevBetaLook.On"/>): one entry per card to jump to it, with Save and
/// Cancel at the bottom so they stay in view while the page scrolls. Built in code and only then; stable keeps the page as it was.
/// </summary>
public partial class SettingsView
{
    private const double MenuWidth = 200;

    private readonly List<(Button Button, Control Card, string Title)> _menu = [];

    /// <summary>The menu's entries that show now, in order, for the checks.</summary>
    internal IReadOnlyList<Button> MenuButtons => _menu.Where(m => m.Button.IsVisible).Select(m => m.Button).ToList();

    /// <summary>The title of the entry that is lit, or null without a menu.</summary>
    internal string? CurrentSection { get; private set; }

    private void BuildSectionMenu()
    {
        PageRoot.ColumnDefinitions.Add(new ColumnDefinition(MenuWidth, GridUnitType.Pixel));
        PageRoot.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        foreach (var child in PageRoot.Children)
            Grid.SetColumn(child, 1);

        var list = new StackPanel { Spacing = 2 };
        var entries = new (string Title, Control Card)[]
        {
            ("Your nest", NestCard), ("This computer", ComputerCard), ("Files to ignore", IgnoreSection), ("Updates", UpdatesCard),
            ("Speed", SpeedCard), ("Where your files live", PlacementCard), ("Help", HelpCard), ("Nests", NestsCard),
            ("About", AboutCard), ("Advanced", AdvancedSection),
        };
        foreach (var (title, card) in entries)
        {
            var button = new Button
            {
                Name = "SectionMenu" + title.Replace(" ", ""),
                Content = title,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(0),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 34,
                Padding = new Thickness(14, 0, 8, 0),
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, title);
            button.Click += OnSectionMenu;
            card.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                    ShowMenuEntries();
            };
            _menu.Add((button, card, title));
            list.Children.Add(button);
        }

        // Save and Cancel leave the header and live at the bottom of the menu.
        HeaderGrid.Children.Remove(SaveButton);
        HeaderGrid.Children.Remove(CancelButton);
        SaveButton.Margin = new Thickness(0, 0, 0, 8);
        SaveButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        SaveButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        CancelButton.Margin = new Thickness(0);
        CancelButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        CancelButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(SaveButton, 0);
        Grid.SetColumn(CancelButton, 0);

        var actions = new StackPanel { Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        actions.Children.Add(SaveButton);
        actions.Children.Add(CancelButton);

        var dock = new DockPanel { Margin = new Thickness(0, 0, 16, 8) };
        DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(actions);
        dock.Children.Add(new ScrollViewer { Content = list });
        Grid.SetColumn(dock, 0);
        Grid.SetRow(dock, 0);
        Grid.SetRowSpan(dock, 2);
        PageRoot.Children.Add(dock);

        Scroller.ScrollChanged += (_, _) => LightCurrentSection();
        Loaded += (_, _) => { ShowMenuEntries(); LightCurrentSection(); };
        ShowMenuEntries();
    }

    private void OnSectionMenu(object? sender, RoutedEventArgs e)
    {
        var (_, card, title) = _menu.First(m => ReferenceEquals(m.Button, sender));
        JumpTo(title, card);
    }

    private void ShowMenuEntries()
    {
        foreach (var (button, card, _) in _menu)
            button.IsVisible = card.IsVisible;
    }

    private double TopOf(Control card) => card.TranslatePoint(new Point(0, 0), Form)?.Y ?? 0;

    // A card near the end cannot reach the top of the view: the entry that was pressed stays lit until the page moves on its own.
    private string? _pinned;
    private double _pinnedOffset;

    private void JumpTo(string title, Control card)
    {
        Scroller.UpdateLayout();
        var most = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var to = Math.Clamp(TopOf(card), 0, most);
        Scroller.Offset = Scroller.Offset.WithY(to);
        _pinned = title;
        _pinnedOffset = to;
        Light(title);
    }

    /// <summary>The lit entry follows the page: the card whose top is the lowest one that has reached the top of the view (two columns, so not just the last).</summary>
    private void LightCurrentSection()
    {
        if (_pinned is not null && Math.Abs(Scroller.Offset.Y - _pinnedOffset) < 2)
            return;
        _pinned = null;
        string? current = null;
        var best = double.NegativeInfinity;
        foreach (var (button, card, title) in _menu)
        {
            if (!button.IsVisible || !card.IsEffectivelyVisible)
                continue;
            var top = TopOf(card);
            if (current is null || (top <= Scroller.Offset.Y + 40 && top > best))
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
        {
            var on = t == title;
            if (on)
                Paint(button, Button.BackgroundProperty, "T.AccentSoft");
            else
                button.Background = Brushes.Transparent;
            Paint(button, Button.ForegroundProperty, on ? "T.AccentInk" : "T.Text");
        }
    }

    private static void Paint(Button button, AvaloniaProperty property, string key) => button.Bind(property, button.GetResourceObservable(key));
}
