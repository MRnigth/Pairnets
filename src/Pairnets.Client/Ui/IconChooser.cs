using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The icon chooser (Pg-DevicesIcon): "Icon for DESKTOP", a close button and the five choices. Picking one
/// raises <see cref="Picked"/> at once; the caller saves it on the nest and closes the popover.
/// </summary>
public sealed class IconChooser : Border
{
    private readonly Dictionary<DeviceIconKind, (Button Button, Border Tile, LineIcon Icon, TextBlock Label, Border Check)> _tiles = [];
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.ExtraBold };
    private DeviceIconKind _selected;

    public IconChooser()
    {
        Width = 340;
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(18);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "T.Card");
        SetResourceReference(BorderBrushProperty, "T.Border");
        SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "T.Text");
        Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 12, Direction = 270, Opacity = 0.35, Color = Colors.Black };

        var root = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel();
        words.Children.Add(_title);
        var note = new TextBlock { Text = "Shown next to this computer everywhere, on all your computers.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSoft");
        words.Children.Add(note);
        head.Children.Add(words);

        var closeIcon = new LineIcon { Width = 12, Height = 12, StrokeThickness = 2.6, Data = Visuals.Resource<Geometry>("I.Close") };
        closeIcon.SetResourceReference(LineIcon.StrokeProperty, "T.Muted");
        var closeBack = new Border { CornerRadius = new CornerRadius(14), Child = closeIcon };
        closeBack.SetResourceReference(Border.BackgroundProperty, "T.Chip");
        var close = new Button { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Template = BareTemplate(), Content = closeBack };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        root.Children.Add(head);

        var grid = new UniformGrid { Columns = 5, Margin = new Thickness(-4, 12, -4, 0) };
        foreach (var kind in DeviceIcon.Choices)
        {
            var icon = new LineIcon { Width = 22, Height = 22, StrokeThickness = 1.8, Data = DeviceIconArt.For(kind), HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = DeviceIcon.Label(kind), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            inner.Children.Add(icon);
            inner.Children.Add(label);
            var checkIcon = new LineIcon { Width = 10, Height = 10, StrokeThickness = 3.2, Stroke = Brushes.White, Data = Visuals.Resource<Geometry>("I.Check") };
            var check = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -6, 0), Visibility = Visibility.Collapsed, Child = checkIcon };
            check.SetResourceReference(Border.BackgroundProperty, "T.Accent");
            var layers = new Grid();
            layers.Children.Add(inner);
            layers.Children.Add(check);
            var tile = new Border { Height = 66, CornerRadius = new CornerRadius(12), Child = layers };
            var cell = new Border { Padding = new Thickness(4, 0, 4, 0), Child = tile };
            var button = new Button { Content = cell, Template = BareTemplate(), Cursor = Cursors.Hand, Tag = kind, FocusVisualStyle = null };
            System.Windows.Automation.AutomationProperties.SetName(button, DeviceIcon.Label(kind));
            var captured = kind;
            button.Click += (_, _) => Picked?.Invoke(captured);
            button.MouseEnter += (_, _) => Paint(captured, hover: true);
            button.MouseLeave += (_, _) => Paint(captured, hover: false);
            _tiles[kind] = (button, tile, icon, label, check);
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        Child = root;
        Selected = DeviceIconKind.Desktop;
    }

    /// <summary>A choice was picked.</summary>
    public event Action<DeviceIconKind>? Picked;

    /// <summary>The close button was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>The computer's name in "Icon for DESKTOP".</summary>
    public string ComputerName
    {
        get => _title.Text;
        set => _title.Text = "Icon for " + value;
    }

    /// <summary>The icon the computer has now (ringed, with a tick).</summary>
    public DeviceIconKind Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            foreach (var kind in _tiles.Keys)
                Paint(kind, hover: false);
        }
    }

    private void Paint(DeviceIconKind kind, bool hover)
    {
        var (button, tile, icon, label, check) = _tiles[kind];
        var chosen = kind == _selected;
        tile.BorderThickness = new Thickness(chosen ? 2 : 1);
        tile.SetResourceReference(Border.BorderBrushProperty, chosen || hover ? "T.Accent" : "T.Border");
        tile.SetResourceReference(Border.BackgroundProperty, chosen ? "T.AccentSoft" : "T.Chip");
        var ink = chosen ? "T.AccentInk" : "T.Text";
        icon.SetResourceReference(LineIcon.StrokeProperty, ink);
        label.SetResourceReference(TextBlock.ForegroundProperty, ink);
        check.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, chosen ? "selected" : string.Empty);
    }

    private static ControlTemplate BareTemplate() =>
        new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

    internal Button ButtonFor(DeviceIconKind kind) => _tiles[kind].Button;

    internal bool IsMarked(DeviceIconKind kind) => _tiles[kind].Check.Visibility == Visibility.Visible;
}
