using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Ui;

/// <summary>Draws a <see cref="DeviceMap"/>: three circles joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    private bool _compact;

    public DeviceMapView()
    {
        InitializeComponent();
    }

    /// <summary>Show the MB/s on the lines (Settings: "Show speed on the map"); never in the small tray picture.</summary>
    public bool ShowSpeed { get; set; } = true;

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
            OtherTick.Width = OtherTick.Height = value ? 16 : 20;
            OtherTickIcon.Width = OtherTickIcon.Height = value ? 8 : 10;
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
        HereLine.UpSpeed = DeviceMap.DotSpeed(map.HereUpSpeed);
        HereLine.DownSpeed = DeviceMap.DotSpeed(map.HereDownSpeed);
        HereLine.Stalled = map.HereStalled;
        OtherLine.Linked = map.OtherLinked;
        OtherLine.Flow = map.OtherFlow;
        OtherLine.UpSpeed = DeviceMap.DotSpeed(map.OtherUpSpeed);
        OtherLine.DownSpeed = DeviceMap.DotSpeed(map.OtherDownSpeed);
        ShowRate(HereRatePill, HereRate, map.HereRateText, map.HereFlow, map.HereStalled);
        ShowRate(OtherRatePill, OtherRate, map.OtherRateText, map.OtherFlow, stalled: false);
        ShowOtherState(map.OtherState);
    }

    /// <summary>The speed above a line, green going up, blue coming down, orange when stalled.</summary>
    private void ShowRate(Border pill, TextBlock text, string? rate, LinkFlow flow, bool stalled)
    {
        var show = ShowSpeed && !_compact && rate is not null;
        pill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;
        text.Text = rate;
        text.SetResourceReference(TextBlock.ForegroundProperty,
            stalled ? "T.WarnText" : flow == LinkFlow.Up ? "T.OkText" : flow == LinkFlow.Down ? "T.Accent" : "T.Text");
        pill.SetResourceReference(Border.BackgroundProperty, stalled ? "T.WarnPill" : "T.Card");
    }

    /// <summary>The other computer's circle breathes while it downloads, and gets a check mark once it has this computer's change.</summary>
    private void ShowOtherState(PeerState state)
    {
        var downloading = state == PeerState.Downloading;
        Motion.Ripple(OtherHalo, downloading && IsVisible);
        if (downloading)
            OtherRing.SetResourceReference(Shape.StrokeProperty, "S.Blue");
        var has = state == PeerState.HasChanges;
        if (has && OtherTick.Visibility != Visibility.Visible)
        {
            OtherTick.Visibility = Visibility.Visible;
            Motion.Pop(OtherTick);
        }
        else if (!has)
        {
            OtherTick.Visibility = Visibility.Collapsed;
        }
        if (has)
            OtherDot.Visibility = Visibility.Collapsed;
        OtherDetail.SetResourceReference(TextBlock.ForegroundProperty, state switch
        {
            PeerState.Downloading => "T.Accent",
            PeerState.HasChanges or PeerState.Sending => "T.OkText",
            _ => "T.Muted",
        });
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
