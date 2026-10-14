using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>Carried by <see cref="DeviceIconPencil.IconChosenEvent"/>: which computer and which icon.</summary>
public sealed class IconChosenEventArgs(RoutedEvent routedEvent, object source, string? deviceId, DeviceIconKind icon) : RoutedEventArgs(routedEvent, source)
{
    public string? DeviceId { get; } = deviceId;

    public DeviceIconKind Icon { get; } = icon;

    /// <summary>What the nest stores ("laptop").</summary>
    public string Wire => DeviceIcon.ToWire(Icon);
}

/// <summary>
/// The small pencil at the corner of a computer's icon square. It opens the <see cref="IconChooser"/> in a popover;
/// picking an icon raises <see cref="IconChosenEvent"/> (it bubbles up to the page) and closes the popover.
/// Put it in the same cell as the icon and bind <see cref="Visual.IsVisible"/> to "the nest can save icons".
/// </summary>
public sealed class DeviceIconPencil : Button
{
    public static readonly RoutedEvent<IconChosenEventArgs> IconChosenEvent =
        RoutedEvent.Register<DeviceIconPencil, IconChosenEventArgs>(nameof(IconChosen), RoutingStrategies.Bubble);

    public static void AddIconChosenHandler(Interactive element, EventHandler<IconChosenEventArgs> handler) => element.AddHandler(IconChosenEvent, handler);

    public static void RemoveIconChosenHandler(Interactive element, EventHandler<IconChosenEventArgs> handler) => element.RemoveHandler(IconChosenEvent, handler);

    public static readonly StyledProperty<string?> DeviceIdProperty = AvaloniaProperty.Register<DeviceIconPencil, string?>(nameof(DeviceId));
    public static readonly StyledProperty<string?> DeviceNameProperty = AvaloniaProperty.Register<DeviceIconPencil, string?>(nameof(DeviceName));
    public static readonly StyledProperty<DeviceIconKind> CurrentProperty = AvaloniaProperty.Register<DeviceIconPencil, DeviceIconKind>(nameof(Current));

    private readonly IconChooser _chooser = new();
    private readonly Flyout _flyout;

    public DeviceIconPencil()
    {
        Width = 20;
        Height = 20;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(-6, -6, 0, 0);
        Padding = new Thickness(0);
        Cursor = new Cursor(StandardCursorType.Hand);
        Template = IconChooser.BareTemplate();
        var pencil = new LineIcon { Width = 10, Height = 10, StrokeThickness = 2.6, Data = StreamGeometry.Parse("M4,20 L8,20 L19,9 L15,5 L4,16 Z"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        IconChooser.Paint(pencil, LineIcon.StrokeProperty, "T.Muted");
        var disc = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = pencil };
        IconChooser.Paint(disc, BackgroundProperty, "T.Card");
        IconChooser.Paint(disc, BorderBrushProperty, "T.Border");
        Content = disc;
        _flyout = new BareFlyout { Content = new Border { Margin = new Thickness(20, 0, 20, 36), Child = _chooser }, Placement = PlacementMode.Bottom, HorizontalOffset = -32, VerticalOffset = 6, ShowMode = FlyoutShowMode.Standard };
        _chooser.Picked += kind =>
        {
            _flyout.Hide();
            RaiseEvent(new IconChosenEventArgs(IconChosenEvent, this, DeviceId, kind));
        };
        _chooser.CloseRequested += () => _flyout.Hide();
        Click += (_, _) =>
        {
            _chooser.ComputerName = DeviceName ?? string.Empty;
            _chooser.Selected = Current;
            if (_flyout.IsOpen)
                _flyout.Hide();
            else
                _flyout.ShowAt(this);
        };
        AttachedToVisualTree += (_, _) => AutomationProperties.SetName(this, "Change the icon for " + DeviceName);
    }

    /// <summary>An icon was picked in the chooser.</summary>
    public event EventHandler<IconChosenEventArgs> IconChosen
    {
        add => AddHandler(IconChosenEvent, value);
        remove => RemoveHandler(IconChosenEvent, value);
    }

    public string? DeviceId
    {
        get => GetValue(DeviceIdProperty);
        set => SetValue(DeviceIdProperty, value);
    }

    public string? DeviceName
    {
        get => GetValue(DeviceNameProperty);
        set => SetValue(DeviceNameProperty, value);
    }

    /// <summary>The computer's icon now (ringed in the chooser).</summary>
    public DeviceIconKind Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    internal IconChooser Chooser => _chooser;

    internal Flyout PopoverFlyout => _flyout;

    /// <summary>A flyout without the theme's frame: the chooser draws its own card and shadow.</summary>
    private sealed class BareFlyout : Flyout
    {
        protected override Control CreatePresenter()
        {
            var presenter = (FlyoutPresenter)base.CreatePresenter();
            presenter.Background = Brushes.Transparent;
            presenter.BorderThickness = new Thickness(0);
            presenter.Padding = new Thickness(0);
            presenter.MinWidth = 0;
            presenter.CornerRadius = new CornerRadius(0);
            return presenter;
        }
    }
}
