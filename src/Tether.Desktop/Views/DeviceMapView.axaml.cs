using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

/// <summary>Draws a <see cref="DeviceMap"/>: three circles joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<DeviceMapView, bool>(nameof(Compact));

    public DeviceMapView()
    {
        InitializeComponent();
    }

    /// <summary>Smaller circles and text, for the tray panel.</summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty)
        {
            Root.Classes.Set("compact", Compact);
            Root.ColumnDefinitions = new ColumnDefinitions(Compact ? "96,*,96,*,96" : "150,*,150,*,150");
        }
    }

    public void Show(DeviceMap map)
    {
        ShowNode(map.Here, HereRing, HereDot, HereName, HereDetail);
        ShowNode(map.Server, ServerRing, ServerDot, ServerName, ServerDetail);
        ShowNode(map.Other, OtherRing, OtherDot, OtherName, OtherDetail);
        HereRing.Classes.Set("online", false); // only the server's ring is green when connected; the computers show a dot
        OtherRing.Classes.Set("online", false);
        OtherMore.Text = map.MoreText;
        OtherMore.IsVisible = map.MoreComputers > 0;
        HereLine.Linked = map.HereLinked;
        HereLine.Flow = map.HereFlow;
        OtherLine.Linked = map.OtherLinked;
        OtherLine.Flow = map.OtherFlow;
    }

    private static void ShowNode(MapNode node, Ellipse ring, Ellipse dot, TextBlock name, TextBlock detail)
    {
        name.Text = node.Name;
        detail.Text = node.Detail;
        ToolTip.SetTip(name, node.Tip);
        ring.Classes.Set("online", node.State == NodeState.Online);
        ring.Classes.Set("unknown", node.State == NodeState.Unknown);
        dot.IsVisible = node.State != NodeState.Unknown;
        dot.Fill = Visuals.Resource<IBrush>(node.State == NodeState.Online ? "S.Green" : "S.Grey");
    }

    // Exposed for the headless UI test.
    internal string OtherText => $"{OtherName.Text}: {OtherDetail.Text}";
    internal bool HereFlowing => HereLine.IsFlowing;
    internal bool OtherFlowing => OtherLine.IsFlowing;
}
