using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Tether.Core.Client;

namespace Tether.Client.Ui;

/// <summary>Draws a <see cref="DeviceMap"/>: three circles joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    private bool _compact;
    private NodeState? _otherState;

    public DeviceMapView()
    {
        InitializeComponent();
    }

    /// <summary>Smaller circles and text, for the tray panel.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            var column = new GridLength(value ? 96 : 150);
            Col0.Width = Col2.Width = Col4.Width = column;
            foreach (var node in new[] { HereNode, OtherNode })
                node.Width = node.Height = value ? 42 : 60;
            ServerNode.Width = ServerNode.Height = value ? 46 : 68;
            foreach (var icon in new[] { HereIcon, ServerIcon, OtherIcon })
                icon.Width = icon.Height = value ? 19 : 26;
            foreach (var dot in new[] { HereDot, ServerDot, OtherDot })
                dot.Width = dot.Height = value ? 12 : 15;
            HereLine.Margin = OtherLine.Margin = value ? new Thickness(-22, 14, -22, 0) : new Thickness(-38, 23, -38, 0);
            foreach (var name in new[] { HereName, ServerName, OtherName })
            {
                name.FontSize = value ? 12 : 13.5;
                name.Margin = new Thickness(0, value ? 5 : 8, 0, 0);
            }
            foreach (var detail in new[] { HereDetail, ServerDetail, OtherDetail, OtherMore })
            {
                detail.FontSize = value ? 11 : 12;
                detail.MaxHeight = value ? 30 : 34;
            }
        }
    }

    public void Show(DeviceMap map)
    {
        ShowNode(map.Here, HereRing, HereDot, HereName, HereDetail, isServer: false);
        ShowNode(map.Server, ServerRing, ServerDot, ServerName, ServerDetail, isServer: true);
        ShowNode(map.Other, OtherRing, OtherDot, OtherName, OtherDetail, isServer: false);
        OtherMore.Text = map.MoreText;
        OtherMore.Visibility = map.MoreComputers > 0 ? Visibility.Visible : Visibility.Collapsed;
        HereLine.Linked = map.HereLinked;
        HereLine.Flow = map.HereFlow;
        OtherLine.Linked = map.OtherLinked;
        OtherLine.Flow = map.OtherFlow;
        if (_otherState == NodeState.Offline && map.Other.State == NodeState.Online)
        {
            Motion.Pop(OtherDot); // the other computer just came online
            Motion.Ripple(OtherRipple);
        }
        _otherState = map.Other.State;
    }

    /// <summary>Only the server's ring turns green when connected; the computers show a coloured dot.</summary>
    private static void ShowNode(MapNode node, Ellipse ring, Ellipse dot, TextBlock name, TextBlock detail, bool isServer)
    {
        name.Text = node.Name;
        detail.Text = node.Detail;
        name.ToolTip = node.Tip;
        var online = isServer && node.State == NodeState.Online;
        ring.SetResourceReference(Shape.StrokeProperty, online ? "S.Green" : "T.NodeRing");
        ring.StrokeThickness = online ? 2 : 1.5;
        ring.StrokeDashArray = node.State == NodeState.Unknown ? new System.Windows.Media.DoubleCollection([2, 2]) : null;
        if (node.State == NodeState.Unknown)
            ring.Fill = System.Windows.Media.Brushes.Transparent;
        else
            ring.SetResourceReference(Shape.FillProperty, "T.Node");
        dot.Visibility = node.State == NodeState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        dot.SetResourceReference(Shape.FillProperty, node.State == NodeState.Online ? "S.Green" : "S.Grey");
    }

    internal string OtherText => $"{OtherName.Text}: {OtherDetail.Text}";

    internal bool HereFlowing => HereLine.IsFlowing;
}
