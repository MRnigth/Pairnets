using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>Draws a <see cref="DeviceMap"/>: three names on one line joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<DeviceMapView, bool>(nameof(Compact));

    public DeviceMapView()
    {
        InitializeComponent();
    }

    /// <summary>Smaller text, for the tray panel.</summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty)
            Root.Classes.Set("compact", Compact);
    }

    public void Show(DeviceMap map)
    {
        ShowNode(map.Here, HereDot, HereName, HereDetail);
        ShowNode(map.Server, ServerDot, ServerName, ServerDetail);
        ShowNode(map.Other, OtherDot, OtherName, OtherDetail);
        OtherMore.Text = map.MoreText;
        OtherMore.IsVisible = map.MoreComputers > 0;
        HereLine.Linked = map.HereLinked;
        HereLine.Flow = map.HereFlow;
        OtherLine.Linked = map.OtherLinked;
        OtherLine.Flow = map.OtherFlow;
    }

    /// <summary>
    /// The speed over this computer's line while files move ("↑ 4.90 MB/s"), and the arrow that travels with it.
    /// <paramref name="speed"/> null means nothing moves; <paramref name="showLabel"/> false keeps the words hidden
    /// (no speed measured yet, or the compact panel, which shows the speed elsewhere).
    /// </summary>
    public void ShowSpeed(string? speed, bool uploading, bool showLabel)
    {
        var moving = speed is not null;
        HereLabel.IsVisible = moving && showLabel && !Compact;
        HereSpeed.Text = speed ?? string.Empty;
        HereArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
        HereArrow.Classes.Set("rise", moving && uploading);
        HereArrow.Classes.Set("fall", moving && !uploading);
    }

    /// <summary>A note over the other computer's line ("DESKTOP uploading"), or null.</summary>
    public void ShowOtherNote(string? note)
    {
        OtherLabel.IsVisible = !Compact && !string.IsNullOrEmpty(note);
        OtherLabelText.Text = note ?? string.Empty;
    }

    private static void ShowNode(MapNode node, Ellipse dot, TextBlock name, TextBlock detail)
    {
        name.Text = node.Name;
        detail.Text = node.Detail;
        ToolTip.SetTip(name, node.Tip);
        dot.IsVisible = node.State != NodeState.Unknown;
        Visuals.Bind(dot, Shape.FillProperty, node.State == NodeState.Online ? "S.Green" : "S.Grey");
    }

    // Exposed for the headless UI test.
    internal string OtherText => $"{OtherName.Text}: {OtherDetail.Text}";
    internal bool HereFlowing => HereLine.IsFlowing;
    internal bool OtherFlowing => OtherLine.IsFlowing;
    internal string ArrowMotion => HereArrow.Classes.Contains("rise") ? "rise" : HereArrow.Classes.Contains("fall") ? "fall" : "none";
}
