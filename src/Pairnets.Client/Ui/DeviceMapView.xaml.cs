using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>Draws a <see cref="DeviceMap"/>: three names on one line joined by <see cref="FlowLine"/>s.</summary>
public partial class DeviceMapView : UserControl
{
    private bool _compact;
    private bool? _arrowUp;

    public DeviceMapView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                Motion.Travel(HereArrow, null); // nothing moves while the window is hidden
            else
                Motion.Travel(HereArrow, _arrowUp);
        };
    }

    /// <summary>Smaller text, for the tray panel.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            foreach (var icon in new[] { HereIcon, ServerIcon, OtherIcon })
            {
                icon.Width = icon.Height = value ? 17 : 22;
                icon.Margin = new Thickness(0, 0, value ? 7 : 10, 0);
            }
            foreach (var dot in new[] { HereDot, ServerDot, OtherDot })
            {
                dot.Width = dot.Height = value ? 6 : 7;
                dot.Margin = new Thickness(value ? 5 : 6, 1, 0, 0);
            }
            foreach (var name in new[] { HereName, ServerName, OtherName })
            {
                name.FontSize = value ? 12.5 : 14;
                name.MaxWidth = value ? 90 : 150;
            }
            foreach (var detail in new[] { HereDetail, ServerDetail, OtherDetail, OtherMore })
            {
                detail.FontSize = value ? 11.5 : 12.5;
                detail.MaxWidth = value ? 96 : 170;
            }
            foreach (var line in new[] { HereLine, OtherLine })
            {
                line.Margin = new Thickness(value ? 8 : 16, 0, value ? 8 : 16, 0);
                line.MinWidth = value ? 18 : 0;
            }
        }
    }

    public void Show(DeviceMap map)
    {
        ShowNode(map.Here, HereDot, HereName, HereDetail);
        ShowNode(map.Server, ServerDot, ServerName, ServerDetail);
        ShowNode(map.Other, OtherDot, OtherName, OtherDetail);
        OtherMore.Text = map.MoreText;
        OtherMore.Visibility = map.MoreComputers > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        HereLabel.Visibility = moving && showLabel && !Compact ? Visibility.Visible : Visibility.Collapsed;
        HereSpeed.Text = speed ?? string.Empty;
        HereArrow.Data = Visuals.Resource<Geometry>(uploading ? "I.Up" : "I.Down");
        _arrowUp = moving ? uploading : null;
        Motion.Travel(HereArrow, IsVisible ? _arrowUp : null);
    }

    /// <summary>A note over the other computer's line ("DESKTOP uploading"), or null.</summary>
    public void ShowOtherNote(string? note)
    {
        OtherLabel.Visibility = !Compact && !string.IsNullOrEmpty(note) ? Visibility.Visible : Visibility.Collapsed;
        OtherLabelText.Text = note ?? string.Empty;
    }

    private static void ShowNode(MapNode node, Ellipse dot, TextBlock name, TextBlock detail)
    {
        name.Text = node.Name;
        detail.Text = node.Detail;
        name.ToolTip = node.Tip;
        dot.Visibility = node.State == NodeState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        dot.SetResourceReference(Shape.FillProperty, node.State == NodeState.Online ? "S.Green" : "S.Grey");
    }

    internal string OtherText => $"{OtherName.Text}: {OtherDetail.Text}";

    internal bool HereFlowing => HereLine.IsFlowing;

    internal bool OtherFlowing => OtherLine.IsFlowing;

    internal string ArrowMotion => _arrowUp switch { true => "rise", false => "fall", _ => "none" };
}
