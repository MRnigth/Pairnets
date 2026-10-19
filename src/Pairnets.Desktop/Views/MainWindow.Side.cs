using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The main window's left side: the open panel (this computer, the nest, the other computers, the update card, the page tiles)
/// and the folded dock. It only draws a <see cref="SidePanelModel"/>; the Windows app draws the same model.
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
            Root.ColumnDefinitions[0].Width = new GridLength(value ? 92 : 320);
            SideOpen.IsVisible = !value;
            SideDock.IsVisible = value;
        }
    }

    private void OnFoldSide(object? sender, RoutedEventArgs e) => SetFolded(true);

    private void OnUnfoldSide(object? sender, RoutedEventArgs e) => SetFolded(false);

    private void SetFolded(bool folded)
    {
        SideFolded = folded;
        SideFoldedChanged?.Invoke(folded);
    }

    /// <summary>The account button opens the Account page.</summary>
    private void OnAccount(object? sender, RoutedEventArgs e) => Navigate(MainPage.Account);

    private void OnThisComputer(object? sender, RoutedEventArgs e) => Navigate(MainPage.Overview);

    private void OnNestRow(object? sender, RoutedEventArgs e) => Navigate(MainPage.Devices);

    private void OnDevices(object? sender, RoutedEventArgs e) => Navigate(MainPage.Devices);

    private void OnFoldGroup(object? sender, RoutedEventArgs e)
    {
        _groupFolded = !_groupFolded;
        GroupGrid.IsVisible = !_groupFolded;
        GroupFoldIcon.Data = Visuals.Resource<Geometry>(_groupFolded ? "I.ChevronDown" : "I.ChevronUp");
        ToolTip.SetTip(GroupFold, _groupFolded ? "Show the group" : "Fold the group");
    }

    // ------------------------------------------------------------------ drawing

    private void RefreshSide()
    {
        if (ThisCard is null)
            return;
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

    private static void Paint(Control target, AvaloniaProperty property, string key) => target.Bind(property, target.GetResourceObservable(key));

    private static void SetTip(Control control, string text)
    {
        ToolTip.SetTip(control, text);
        AutomationProperties.SetName(control, text);
    }

    private void DrawOpen(SidePanelModel m)
    {
        AccountInitial.Text = m.AccountInitial;
        SetTip(AccountButton, m.AccountLabel);

        // This computer
        var t = m.This;
        if (t.Highlighted)
            Paint(ThisCard, BackgroundProperty, PillKey(t.Tone));
        else
            ThisCard.Background = Brushes.Transparent;
        Paint(ThisIconSq, Border.BackgroundProperty, t.Highlighted ? "T.Card" : "T.Chip");
        ThisIcon.Data = IconOf(t.Icon);
        Paint(ThisDot, Shape.FillProperty, DotKey(t.Dot));
        ThisName.Text = t.Name;
        ThisSub.Text = t.Text;
        Paint(ThisSub, TextBlock.ForegroundProperty, InkKey(t.Tone));
        ToolTip.SetTip(ThisCard, t.Tooltip);
        ThisBar.IsVisible = t.ShowBar;
        if (t.BarPercent is { } percent)
            ThisBar.Value = percent;

        // The nest
        var n = m.Nest;
        if (n.Tone == SideTone.Neutral)
            NestRow.Background = Brushes.Transparent;
        else
            Paint(NestRow, BackgroundProperty, PillKey(n.Tone));
        Paint(NestDot, Shape.FillProperty, DotKey(n.Dot));
        NestSub.Text = n.Text;
        Paint(NestSub, TextBlock.ForegroundProperty, InkKey(n.Tone));
        ToolTip.SetTip(NestRow, n.Tooltip);

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
        NavAttention.Classes.Set("amber", m.Tiles.AttentionAlert);
        AutomationProperties.SetName(NavAttention, m.Tiles.AttentionAlert ? $"Needs you, {m.Tiles.AttentionCount}" : "Needs you");
    }

    private void DrawDock(SidePanelModel m)
    {
        DockAccountInitial.Text = m.AccountInitial;
        SetTip(DockAccount, m.AccountLabel);

        DockBadge.IsVisible = m.Tiles.AttentionAlert;
        DockBadgeText.Text = m.Tiles.AttentionNote;
        SetTip(DockAttention, m.Tiles.AttentionAlert ? $"Needs you, {m.Tiles.AttentionCount}" : "Needs you");

        // This computer, with a ring around it while it syncs
        DockThisIcon.Data = IconOf(m.This.Icon);
        SetTip(DockThis, m.This.Tooltip);
        var ring = m.RingPercent;
        DockRingTrack.IsVisible = DockRing.IsVisible = ring is not null;
        if (ring is { } p)
        {
            // Dashes count in line widths: the circle is 41.5 px across, the line 2.5 px thick.
            var whole = Math.PI * 41.5 / 2.5;
            DockRing.StrokeDashArray = [Math.Max(0.01, whole * p / 100.0), whole + 1];
        }

        Paint(DockNestDot, Shape.FillProperty, DotKey(m.Nest.Dot));
        SetTip(DockNest, m.Nest.Tooltip);

        DrawDockOther(DockOther1, DockOther1Icon, DockOther1Dot, m.DockOthers.ElementAtOrDefault(0));
        DrawDockOther(DockOther2, DockOther2Icon, DockOther2Dot, m.DockOthers.ElementAtOrDefault(1));

        DockMore.IsVisible = m.DockMoreCount > 0;
        DockMoreText.Text = m.DockMoreText;
        ToolTip.SetTip(DockMore, string.Join("\n", m.DockMore.Select(o => $"{o.Name}   {o.Text}")));
        AutomationProperties.SetName(DockMore, m.DockMoreLabel);
    }

    private static void DrawDockOther(Button button, LineIcon icon, Ellipse dot, SideOther? other)
    {
        button.IsVisible = other is not null;
        if (other is null)
            return;
        icon.Data = IconOf(other.Icon);
        Paint(icon, LineIcon.StrokeProperty, other.Reachable ? "T.Text" : "T.TextSoft");
        Paint(dot, Shape.FillProperty, DotKey(other.Dot));
        Paint(button, BackgroundProperty, other.Reachable ? "T.Chip" : "T.ChipDim");
        SetTip(button, other.Tooltip);
    }

    // ------------------------------------------------------------------ cells of the group

    private Button OtherCell(SideOther o)
    {
        var reachable = o.Reachable;
        var circle = new Ellipse { Width = 32, Height = 32, StrokeThickness = 1 };
        Paint(circle, Shape.FillProperty, reachable ? "T.Chip" : "T.ChipDim");
        switch (o.Kind)
        {
            case OtherKind.Live:
                Paint(circle, Shape.StrokeProperty, "T.Accent");
                break;
            case OtherKind.CantReach:
                Paint(circle, Shape.StrokeProperty, "T.Bad");
                break;
            case OtherKind.LastSeen:
                Paint(circle, Shape.StrokeProperty, "T.Dash");
                break;
            default:
                circle.Stroke = Brushes.Transparent;
                break;
        }
        if (!reachable)
            circle.StrokeDashArray = [3, 2];

        var icon = new LineIcon { Width = 15, Height = 15, StrokeThickness = 1.9, Data = IconOf(o.Icon) };
        Paint(icon, LineIcon.StrokeProperty, reachable ? "T.Text" : "T.TextSoft");
        var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1) };
        Paint(dot, Shape.StrokeProperty, "T.Tile");
        Paint(dot, Shape.FillProperty, DotKey(o.Dot));
        var picture = new Grid { Width = 32, Height = 32, Children = { circle, icon, dot } };

        var name = new TextBlock { Text = o.Name, FontSize = 11, FontWeight = FontWeight.ExtraBold, MaxWidth = 82, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        Paint(name, TextBlock.ForegroundProperty, reachable ? "T.Text" : "T.Muted");
        var sub = new TextBlock { Text = o.Text, FontSize = 11, FontWeight = FontWeight.Bold, MaxWidth = 82, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        Paint(sub, TextBlock.ForegroundProperty, o.Kind switch
        {
            OtherKind.Live => "T.AccentInk",
            OtherKind.Ok => "T.OkText",
            OtherKind.CantReach => "T.BadText",
            _ => "T.TextSoft",
        });
        var button = new Button
        {
            Tag = "SideCell",
            Margin = new Thickness(2, 4, 2, 4),
            Content = new StackPanel { Children = { picture, name, sub } },
        };
        button.Classes.Add("sidecell");
        SetTip(button, o.Tooltip);
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
        var circle = new Ellipse { Width = 32, Height = 32, StrokeThickness = 1.5, StrokeDashArray = [3, 2] };
        Paint(circle, Shape.StrokeProperty, "T.Dash");
        Control inside;
        if (glyph is null)
        {
            var plus = new LineIcon { Width = 14, Height = 14, StrokeThickness = 2.4, Data = Visuals.Resource<Geometry>("I.Plus") };
            Paint(plus, LineIcon.StrokeProperty, "T.Muted");
            inside = plus;
        }
        else
        {
            var text = new TextBlock { Text = glyph, FontSize = 11, FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Paint(text, TextBlock.ForegroundProperty, "T.Text");
            inside = text;
        }
        var name = new TextBlock { Text = glyph is null ? label : "more", FontSize = 11, FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        Paint(name, TextBlock.ForegroundProperty, "T.Muted");
        var button = new Button
        {
            Tag = glyph is null ? "SideAdd" : "SideCell",
            Margin = new Thickness(2, 4, 2, 4),
            Content = new StackPanel { Children = { new Grid { Width = 32, Height = 32, Children = { circle, inside } }, name } },
        };
        button.Classes.Add("sidecell");
        SetTip(button, tip);
        return button;
    }
}
