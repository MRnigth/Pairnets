using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

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
/// Put it in the same cell as the icon and bind <see cref="UIElement.Visibility"/> to "the nest can save icons".
/// </summary>
public sealed class DeviceIconPencil : Button
{
    public static readonly RoutedEvent IconChosenEvent = EventManager.RegisterRoutedEvent(
        "IconChosen", RoutingStrategy.Bubble, typeof(EventHandler<IconChosenEventArgs>), typeof(DeviceIconPencil));

    public static void AddIconChosenHandler(DependencyObject element, EventHandler<IconChosenEventArgs> handler) =>
        (element as UIElement)?.AddHandler(IconChosenEvent, handler);

    public static void RemoveIconChosenHandler(DependencyObject element, EventHandler<IconChosenEventArgs> handler) =>
        (element as UIElement)?.RemoveHandler(IconChosenEvent, handler);

    public static readonly DependencyProperty DeviceIdProperty = DependencyProperty.Register(nameof(DeviceId), typeof(string), typeof(DeviceIconPencil));
    public static readonly DependencyProperty DeviceNameProperty = DependencyProperty.Register(nameof(DeviceName), typeof(string), typeof(DeviceIconPencil));
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(nameof(Current), typeof(DeviceIconKind), typeof(DeviceIconPencil), new PropertyMetadata(DeviceIconKind.Desktop));

    private readonly IconChooser _chooser = new();
    private readonly Popup _popup;

    public DeviceIconPencil()
    {
        Width = 24;
        Height = 24;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(-8, -8, 0, 0); // the same middle as the old 20 px disc
        Cursor = Cursors.Hand;
        SetResourceReference(FocusVisualStyleProperty, "FocusRingRound");
        Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var pencil = new LineIcon { Width = 10, Height = 10, StrokeThickness = 2.6, Data = Geometry.Parse("M4,20 L8,20 L19,9 L15,5 L4,16 Z") };
        pencil.SetResourceReference(LineIcon.StrokeProperty, "T.Muted");
        var disc = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = pencil };
        disc.SetResourceReference(Border.BackgroundProperty, "T.Card");
        disc.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        pencil.HorizontalAlignment = HorizontalAlignment.Center;
        pencil.VerticalAlignment = VerticalAlignment.Center;
        Content = disc;
        _popup = new Popup
        {
            Child = new Border { Margin = new Thickness(20, 0, 20, 36), Child = _chooser },
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Placement = PlacementMode.Bottom,
            PlacementTarget = this,
            HorizontalOffset = -32,
            VerticalOffset = 6,
        };
        _chooser.Picked += kind =>
        {
            _popup.IsOpen = false;
            RaiseEvent(new IconChosenEventArgs(IconChosenEvent, this, DeviceId, kind));
        };
        _chooser.CloseRequested += () => _popup.IsOpen = false;
        Click += (_, _) =>
        {
            _chooser.ComputerName = DeviceName ?? string.Empty;
            _chooser.Selected = Current;
            _popup.IsOpen = !_popup.IsOpen;
        };
        Loaded += (_, _) => System.Windows.Automation.AutomationProperties.SetName(this, "Change the icon for " + DeviceName);
    }

    public string? DeviceId
    {
        get => (string?)GetValue(DeviceIdProperty);
        set => SetValue(DeviceIdProperty, value);
    }

    public string? DeviceName
    {
        get => (string?)GetValue(DeviceNameProperty);
        set => SetValue(DeviceNameProperty, value);
    }

    /// <summary>The computer's icon now (ringed in the chooser).</summary>
    public DeviceIconKind Current
    {
        get => (DeviceIconKind)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    internal IconChooser Chooser => _chooser;

    internal Popup Popup => _popup;
}
