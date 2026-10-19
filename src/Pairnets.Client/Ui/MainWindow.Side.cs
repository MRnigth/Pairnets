using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The main window's left side: the open panel (this computer, the nest, the other computers, the update card, the page tiles)
/// and the folded dock. It only draws a <see cref="SidePanelModel"/>; the Mac/Linux app draws the same model.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<SideOther>? _shownOthers;
    private bool _shownMore;
    private bool _groupFolded;
    private bool _sideFolded;

    /// <summary>The user folded or opened the left side (the controller remembers it).</summary>
    public event Action<bool>? SideFoldedChanged;

    /// <summary>True when the left side is folded into the dock. Set from the saved settings when the window opens.</summary>
    public bool SideFolded
    {
        get => _sideFolded;
        set
        {
            _sideFolded = value;
            SideColumn.Width = new GridLength(value ? 92 : 320);
            SideOpen.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            SideDock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnFoldSide(object sender, RoutedEventArgs e) => SetFolded(true);

    private void OnUnfoldSide(object sender, RoutedEventArgs e) => SetFolded(false);

    private void SetFolded(bool folded)
    {
        SideFolded = folded;
        SideFoldedChanged?.Invoke(folded);
    }

    /// <summary>The account button opens the Account page.</summary>
    private void OnAccount(object sender, RoutedEventArgs e) => Navigate(MainPage.Account);

    private void OnThisComputer(object sender, RoutedEventArgs e) => Navigate(MainPage.Overview);

    private void OnNestRow(object sender, RoutedEventArgs e) => Navigate(MainPage.Devices);

    private void OnDevices(object sender, RoutedEventArgs e) => Navigate(MainPage.Devices);

    private void OnFoldGroup(object sender, RoutedEventArgs e)
    {
        _groupFolded = !_groupFolded;
        GroupGrid.Visibility = Show(!_groupFolded);
        GroupFoldIcon.Data = Visuals.Resource<Geometry>(_groupFolded ? "I.ChevronDown" : "I.ChevronUp");
        GroupFold.ToolTip = _groupFolded ? "Show the group" : "Fold the group";
    }

    // ------------------------------------------------------------------ drawing

    private void RefreshSide()
    {
        if (ThisCard is null)
            return; // Checked fires during InitializeComponent
        var m = SidePanelModel.Build(_status, ThisDevice, _activityItems.Count, _attentionCount, _update, DateTimeOffset.UtcNow);
        DrawOpen(m);
        DrawDock(m);
    }

    private static string PillKey(SideTone tone) => tone switch
    {
        SideTone.Accent => "T.AccentSoft",
        SideTone.Warn => "T.WarnPill",
        _ => "T.BadPill",
    };

    private static string InkKey(SideTone tone) => tone switch
    {
        SideTone.Accent => "T.AccentInk",
        SideTone.Warn => "T.WarnText",
        SideTone.Bad => "T.BadText",
        _ => "T.TextSoft",
    };

    private static string DotKey(SideDot dot) => dot switch
    {
        SideDot.Warn => "T.Warn",
        SideDot.Bad => "T.Bad",
        SideDot.Off => "T.Off",
        _ => "T.Ok",
    };

    private static Geometry? IconOf(DeviceIconKind icon) => DeviceIconArt.For(icon);

    private void DrawOpen(SidePanelModel m)
    {
        AccountInitial.Text = m.AccountInitial;
        AccountButton.ToolTip = m.AccountLabel;
        System.Windows.Automation.AutomationProperties.SetName(AccountButton, m.AccountLabel);

        // This computer
        var t = m.This;
        if (t.Highlighted)
            ThisCard.SetResourceReference(BackgroundProperty, PillKey(t.Tone));
        else
            ThisCard.Background = Brushes.Transparent;
        ThisIconSq.SetResourceReference(Border.BackgroundProperty, t.Highlighted ? "T.Card" : "T.Chip");
        ThisIcon.Data = IconOf(t.Icon);
        ThisDot.SetResourceReference(Shape.FillProperty, DotKey(t.Dot));
        ThisName.Text = t.Name;
        ThisSub.Text = t.Text;
        ThisSub.SetResourceReference(TextBlock.ForegroundProperty, InkKey(t.Tone));
        ThisCard.ToolTip = t.Tooltip;
        ThisBar.Visibility = Show(t.ShowBar);
        if (t.BarPercent is { } percent)
            Motion.Glide(ThisBar, percent);

        // The nest
        var n = m.Nest;
        if (n.Tone == SideTone.Neutral)
            NestRow.Background = Brushes.Transparent;
        else
            NestRow.SetResourceReference(BackgroundProperty, PillKey(n.Tone));
        NestDot.SetResourceReference(Shape.FillProperty, DotKey(n.Dot));
        NestSub.Text = n.Text;
        NestSub.SetResourceReference(TextBlock.ForegroundProperty, InkKey(n.Tone));
        NestRow.ToolTip = n.Tooltip;

        // The other computers
        GroupNote.Text = m.GroupNote;
        if (_shownOthers is null || !_shownOthers.SequenceEqual(m.Others) || _shownMore != m.ShowsMore)
        {
            _shownOthers = m.Others;
            _shownMore = m.ShowsMore;
            GroupGrid.Children.Clear();
            foreach (var other in m.Others)
                GroupGrid.Children.Add(OtherCell(other));
            GroupGrid.Children.Add(m.ShowsMore ? MoreCell(m.MoreText, m.MoreCount) : AddCell());
        }

        // The page tiles
        OverviewNote.Text = m.Tiles.OverviewNote;
        ActivityNote.Text = m.Tiles.ActivityNote;
        AttentionCount.Text = m.Tiles.AttentionNote;
        DevicesNote.Text = m.Tiles.DevicesNote;
        NavAttention.SetResourceReference(StyleProperty, m.Tiles.AttentionAlert ? "SideTileAmber" : "SideTile");
        System.Windows.Automation.AutomationProperties.SetName(NavAttention, m.Tiles.AttentionAlert ? $"Needs you, {m.Tiles.AttentionCount}" : "Needs you");
    }

    private void DrawDock(SidePanelModel m)
    {
        DockAccountInitial.Text = m.AccountInitial;
        DockAccount.ToolTip = m.AccountLabel;
        System.Windows.Automation.AutomationProperties.SetName(DockAccount, m.AccountLabel);

        DockBadge.Visibility = Show(m.Tiles.AttentionAlert);
        DockBadgeText.Text = m.Tiles.AttentionNote;
        DockAttention.ToolTip = m.Tiles.AttentionAlert ? $"Needs you, {m.Tiles.AttentionCount}" : "Needs you";
        System.Windows.Automation.AutomationProperties.SetName(DockAttention, (string)DockAttention.ToolTip);

        // This computer, with a ring around it while it syncs
        DockThisIcon.Data = IconOf(m.This.Icon);
        DockThis.ToolTip = m.This.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(DockThis, m.This.Tooltip);
        var ring = m.RingPercent;
        DockRingTrack.Visibility = DockRing.Visibility = Show(ring is not null);
        if (ring is { } p)
        {
            // Dashes count in line widths: the circle is 41.5 px across, the line 2.5 px thick.
            var whole = Math.PI * 41.5 / 2.5;
            DockRing.StrokeDashArray = new DoubleCollection { Math.Max(0.01, whole * p / 100.0), whole + 1 };
        }

        DockNestDot.SetResourceReference(Shape.FillProperty, DotKey(m.Nest.Dot));
        DockNest.ToolTip = m.Nest.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(DockNest, m.Nest.Tooltip);

        DrawDockOther(DockOther1, DockOther1Icon, DockOther1Dot, m.DockOthers.ElementAtOrDefault(0));
        DrawDockOther(DockOther2, DockOther2Icon, DockOther2Dot, m.DockOthers.ElementAtOrDefault(1));

        DockMore.Visibility = Show(m.DockMoreCount > 0);
        DockMoreText.Text = m.DockMoreText;
        var tip = string.Join("\n", m.DockMore.Select(o => $"{o.Name}   {o.Text}"));
        DockMore.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(DockMore, m.DockMoreLabel);
    }

    private static void DrawDockOther(Button button, LineIcon icon, Ellipse dot, SideOther? other)
    {
        button.Visibility = Show(other is not null);
        if (other is null)
            return;
        icon.Data = IconOf(other.Icon);
        icon.SetResourceReference(LineIcon.StrokeProperty, other.Reachable ? "T.Text" : "T.TextSoft");
        dot.SetResourceReference(Shape.FillProperty, DotKey(other.Dot));
        button.SetResourceReference(BackgroundProperty, other.Reachable ? "T.Chip" : "T.ChipDim");
        button.ToolTip = other.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(button, other.Tooltip);
    }

    // ------------------------------------------------------------------ cells of the group

    private Button OtherCell(SideOther o)
    {
        var reachable = o.Reachable;
        var circle = new Ellipse { Width = 32, Height = 32, StrokeThickness = 1 };
        circle.SetResourceReference(Shape.FillProperty, reachable ? "T.Chip" : "T.ChipDim");
        switch (o.Kind)
        {
            case OtherKind.Live:
                circle.SetResourceReference(Shape.StrokeProperty, "T.Accent");
                break;
            case OtherKind.CantReach:
                circle.SetResourceReference(Shape.StrokeProperty, "T.Bad");
                break;
            case OtherKind.LastSeen:
                circle.SetResourceReference(Shape.StrokeProperty, "T.Dash");
                break;
            default:
                circle.Stroke = Brushes.Transparent;
                break;
        }
        if (!reachable)
            circle.StrokeDashArray = new DoubleCollection { 3, 2 };

        var icon = new LineIcon { Width = 15, Height = 15, StrokeThickness = 1.9, Data = IconOf(o.Icon) };
        icon.SetResourceReference(LineIcon.StrokeProperty, reachable ? "T.Text" : "T.TextSoft");
        var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1) };
        dot.SetResourceReference(Shape.StrokeProperty, "T.Tile");
        dot.SetResourceReference(Shape.FillProperty, DotKey(o.Dot));
        var picture = new Grid { Width = 32, Height = 32, Children = { circle, icon, dot } };

        var name = new TextBlock { Text = o.Name, FontSize = 11, FontWeight = FontWeights.ExtraBold, MaxWidth = 82, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, reachable ? "T.Text" : "T.Muted");
        var sub = new TextBlock { Text = o.Text, FontSize = 11, FontWeight = FontWeights.Bold, MaxWidth = 82, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, o.Kind switch
        {
            OtherKind.Live => "T.AccentInk",
            OtherKind.Ok => "T.OkText",
            OtherKind.CantReach => "T.BadText",
            _ => "T.TextSoft",
        });
        var button = new Button
        {
            Style = (Style)FindResource("SideCell"),
            Tag = "SideCell",
            Margin = new Thickness(2, 4, 2, 4),
            ToolTip = o.Tooltip,
            Content = new StackPanel { Children = { picture, name, sub } },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, o.Tooltip);
        button.Click += OnDevices;
        return button;
    }

    private Button AddCell()
    {
        var button = DashedCell(label: "Add", glyph: null, "Add a computer");
        button.Click += OnAddComputer;
        return button;
    }

    private Button MoreCell(string text, int count)
    {
        var button = DashedCell(label: text, glyph: text, count == 1 ? "1 more computer" : $"{count} more computers");
        button.Click += OnDevices;
        return button;
    }

    /// <summary>A dashed circle with a plus ("Add") or a count ("+3") and a label under it.</summary>
    private Button DashedCell(string label, string? glyph, string tip)
    {
        var circle = new Ellipse { Width = 32, Height = 32, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } };
        circle.SetResourceReference(Shape.StrokeProperty, "T.Dash");
        FrameworkElement inside;
        if (glyph is null)
        {
            var plus = new LineIcon { Width = 14, Height = 14, StrokeThickness = 2.4, Data = Visuals.Resource<Geometry>("I.Plus") };
            plus.SetResourceReference(LineIcon.StrokeProperty, "T.Muted");
            inside = plus;
        }
        else
        {
            var text = new TextBlock { Text = glyph, FontSize = 11, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
            inside = text;
        }
        var name = new TextBlock { Text = glyph is null ? label : "more", FontSize = 11, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
        var button = new Button
        {
            Style = (Style)FindResource("SideCell"),
            Tag = glyph is null ? "SideAdd" : "SideCell",
            Margin = new Thickness(2, 4, 2, 4),
            ToolTip = tip,
            Content = new StackPanel { Children = { new Grid { Width = 32, Height = 32, Children = { circle, inside } }, name } },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }
}
