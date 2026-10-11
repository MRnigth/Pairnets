using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>Draws a <see cref="DeviceMap"/>: three circles joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<DeviceMapView, bool>(nameof(Compact));

    public DeviceMapView()
    {
        InitializeComponent();
    }

    /// <summary>Show the MB/s on the lines (Settings: "Show speed on the map"); never in the small tray picture.</summary>
    public bool ShowSpeed { get; set; } = true;

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
        pill.IsVisible = ShowSpeed && !Compact && rate is not null;
        if (!pill.IsVisible)
            return;
        text.Text = rate;
        pill.Classes.Set("up", !stalled && flow == LinkFlow.Up);
        pill.Classes.Set("down", !stalled && flow == LinkFlow.Down);
        pill.Classes.Set("stalled", stalled);
    }

    /// <summary>The other computer's circle breathes while it downloads, and gets a check mark once it has this computer's change.</summary>
    private void ShowOtherState(PeerState state)
    {
        var downloading = state == PeerState.Downloading;
        OtherHalo.IsVisible = downloading;
        OtherHalo.Classes.Set("ripple", downloading);
        OtherRing.Classes.Set("downloading", downloading);
        var has = state == PeerState.HasChanges;
        if (has && !OtherTick.IsVisible)
        {
            OtherTick.IsVisible = true;
            OtherTick.Classes.Remove("pop");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OtherTick.Classes.Add("pop"), Avalonia.Threading.DispatcherPriority.Background);
        }
        else if (!has)
        {
            OtherTick.IsVisible = false;
        }
        if (has)
            OtherDot.IsVisible = false;
        OtherDetail.Classes.Set("down", downloading);
        OtherDetail.Classes.Set("ok", state is PeerState.HasChanges or PeerState.Sending);
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
    internal string? HereRateShown => HereRatePill.IsVisible ? HereRate.Text : null;
    internal bool OtherBreathes => OtherHalo.IsVisible && OtherHalo.Classes.Contains("ripple");
    internal bool OtherHasTick => OtherTick.IsVisible;
}
