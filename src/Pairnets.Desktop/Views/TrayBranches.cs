using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Draws the small branches of the tray panel: from the nest down to each online computer (a line, the speed on it, the
/// computer's icon, name and status), the one red row for the computers that can't be reached, and the rows of "Latest
/// changes". It only draws a <see cref="TrayPanelModel"/>; the Windows app draws the same.
/// </summary>
internal static class TrayBranches
{
    private const double LineCell = 108;

    public static void Fill(StackPanel host, TrayPanelModel m)
    {
        host.Children.Clear();
        var rows = m.Branches.ToList();
        var last = m.Unreachable is null ? rows.Count - 1 : -1;
        for (var i = 0; i < rows.Count; i++)
            host.Children.Add(Row(rows[i], elbow: i == last));
        if (m.Unreachable is { } gone)
            host.Children.Add(Unreachable(gone));
    }

    public static void FillChanges(StackPanel host, IReadOnlyList<TrayChange> changes)
    {
        host.Children.Clear();
        foreach (var change in changes)
            host.Children.Add(Change(change));
    }

    private static void Paint(Control target, AvaloniaProperty property, string key) => target.Bind(property, target.GetResourceObservable(key));

    private static string DotKey(NestDot dot) => dot switch
    {
        NestDot.Warn => "T.Warn",
        NestDot.Bad => "T.Bad",
        NestDot.Off => "T.Off",
        _ => "T.Ok",
    };

    private static string LineKey(NestLineKind kind) => kind switch
    {
        NestLineKind.Active => "T.Accent",
        NestLineKind.Ok => "T.OkLine",
        NestLineKind.Warn => "T.Warn",
        NestLineKind.Bad or NestLineKind.Unreachable => "T.Bad",
        _ => "T.Line",
    };

    private static string InkKey(TrayInk ink) => ink switch
    {
        TrayInk.Accent => "T.AccentInk",
        TrayInk.Ok => "T.OkText",
        TrayInk.Bad => "T.BadText",
        _ => "T.TextSoft",
    };

    private static string WordKey(NestLineKind kind) => kind switch
    {
        NestLineKind.Ok => "T.OkText",
        NestLineKind.Warn => "T.WarnText",
        NestLineKind.Bad or NestLineKind.Unreachable => "T.BadText",
        _ => "T.TextSoft",
    };

    private static string IconKey(DeviceIconKind icon) => icon switch
    {
        DeviceIconKind.Laptop => "I.Laptop",
        DeviceIconKind.Office => "I.Office",
        DeviceIconKind.Mini => "I.MiniPc",
        DeviceIconKind.Tv => "I.Tv",
        _ => "I.Desktop",
    };

    /// <summary>A horizontal line that fills its cell: solid, or dashed.</summary>
    private static Control Dash(string brushKey, bool dashed)
    {
        if (!dashed)
        {
            var solid = new Border { Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center };
            Paint(solid, Border.BackgroundProperty, brushKey);
            return solid;
        }
        var line = new Line { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), Stretch = Stretch.Fill, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, StrokeDashArray = [2.5, 2] };
        Paint(line, Shape.StrokeProperty, brushKey);
        return line;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        Paint(t, TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    /// <summary>The 108-wide cell with the vertical stub on its left, which ends halfway on the last row.</summary>
    private static Grid Stub(bool elbow)
    {
        var cell = new Grid { Width = LineCell, Height = 24 };
        var stub = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Height = elbow ? 13 : 24 };
        Paint(stub, Border.BackgroundProperty, "T.Line");
        cell.Children.Add(stub);
        return cell;
    }

    private static Border Pill(string text)
    {
        var pill = new Border { Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(pill, Border.BackgroundProperty, "T.AccentSoft");
        var label = Text(text, 11, FontWeight.ExtraBold, "T.AccentInk");
        label.TextTrimming = TextTrimming.None;
        pill.Child = label;
        return pill;
    }

    private static Grid BranchRow(string name)
    {
        var row = new Grid { Height = 24, ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        AutomationProperties.SetName(row, name);
        ToolTip.SetTip(row, name);
        return row;
    }

    private static Grid Row(TrayBranch b, bool elbow)
    {
        var row = BranchRow(b.IsThis ? b.Name + ", this computer, " + (b.Word ?? b.Pill ?? string.Empty) : b.Name + ", " + b.Side);

        // The line from the nest
        var cell = Stub(elbow);
        var inner = new Grid { Margin = new Thickness(2, 0, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto,6") };
        var key = LineKey(b.Kind);
        var first = Dash(key, b.Dashed);
        first.Margin = new Thickness(0, 0, b.HasPill || b.HasWord ? 4 : 0, 0);
        inner.Children.Add(first);
        if (b.HasPill || b.HasWord)
        {
            Control middle = b.HasPill ? Pill(b.Pill!) : Text(b.Word!, 11, FontWeight.Bold, WordKey(b.Kind));
            Grid.SetColumn(middle, 1);
            inner.Children.Add(middle);
            var tail = Dash(key, b.Dashed);
            tail.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(tail, 2);
            inner.Children.Add(tail);
        }
        else
        {
            Grid.SetColumnSpan(first, 3);
        }
        cell.Children.Add(inner);
        row.Children.Add(cell);

        // The computer's icon with its dot
        var dim = b.Dot is NestDot.Off or NestDot.Bad;
        var square = new Border { CornerRadius = new CornerRadius(8) };
        Paint(square, Border.BackgroundProperty, dim ? "T.ChipDim" : "T.Chip");
        var icon = new LineIcon { Width = 13, Height = 13, StrokeThickness = 2, Data = Visuals.Resource<Geometry>(IconKey(b.Icon)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Paint(icon, LineIcon.StrokeProperty, dim ? "T.TextSoft" : "T.Text");
        var dot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
        Paint(dot, Shape.StrokeProperty, "T.Card");
        Paint(dot, Shape.FillProperty, DotKey(b.Dot));
        var picture = new Grid { Width = 24, Height = 24, Margin = new Thickness(8, 0, 8, 0), Children = { square, icon, dot } };
        Grid.SetColumn(picture, 1);
        row.Children.Add(picture);

        var name = Text(b.Name, 12.5, FontWeight.ExtraBold, "T.Text");
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var side = Text(b.Side, 11.5, b.SideInk == TrayInk.Muted ? FontWeight.Normal : FontWeight.SemiBold, InkKey(b.SideInk));
        side.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(side, 3);
        row.Children.Add(side);
        return row;
    }

    private static Grid Unreachable(TrayUnreachable gone)
    {
        var row = BranchRow(gone.Tooltip);

        // A dashed red line with a red cross on it; the vertical stub stops at the line
        var cell = Stub(elbow: true);
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,14") };
        var first = Dash("T.Bad", dashed: true);
        first.Margin = new Thickness(0, 0, 3, 0);
        inner.Children.Add(first);
        var cross = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center };
        Paint(cross, Border.BackgroundProperty, "T.BadPill");
        Paint(cross, Border.BorderBrushProperty, "T.Bad");
        var x = new LineIcon { Width = 9, Height = 9, StrokeThickness = 3.2, Data = Visuals.Resource<Geometry>("I.X"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Paint(x, LineIcon.StrokeProperty, "T.Bad");
        cross.Child = x;
        AutomationProperties.SetName(cross, "Can't reach them");
        Grid.SetColumn(cross, 1);
        inner.Children.Add(cross);
        var tail = Dash("T.Bad", dashed: true);
        tail.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(tail, 2);
        inner.Children.Add(tail);
        cell.Children.Add(inner);
        row.Children.Add(cell);

        // The pile of up to three small dashed computers
        var pile = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        for (var i = 0; i < gone.Icons.Count; i++)
        {
            var dash = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(i == 0 ? 0 : -5, 0, 0, 0) };
            Paint(dash, Border.BackgroundProperty, "T.ChipDim");
            Paint(dash, Border.BorderBrushProperty, "T.Bad");
            var glyph = new LineIcon { Width = 10, Height = 10, StrokeThickness = 2.2, Data = Visuals.Resource<Geometry>(IconKey(gone.Icons[i])), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Paint(glyph, LineIcon.StrokeProperty, "T.TextSoft");
            dash.Child = glyph;
            pile.Children.Add(dash);
        }
        Grid.SetColumn(pile, 1);
        row.Children.Add(pile);

        var text = Text(gone.Text, 12.5, FontWeight.Bold, "T.BadText");
        Grid.SetColumn(text, 2);
        Grid.SetColumnSpan(text, 2);
        row.Children.Add(text);
        return row;
    }

    // ------------------------------------------------------------------ latest changes

    private static Grid Change(TrayChange c)
    {
        var (glyph, ink) = c.Kind switch
        {
            TrayChangeKind.Down => ("I.Down", "T.OkText"),
            TrayChangeKind.Warn => ("I.Warning", "T.WarnText"),
            TrayChangeKind.Bad => ("I.X", "T.BadText"),
            TrayChangeKind.Muted => ("I.Info", "T.TextSoft"),
            _ => ("I.Up", "T.AccentInk"),
        };
        var row = new Grid { Margin = new Thickness(0, 4), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var square = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(square, Border.BackgroundProperty, c.Live ? "T.AccentSoft" : "T.Chip");
        var icon = new LineIcon { Width = 14, Height = 14, StrokeThickness = 2.4, Data = Visuals.Resource<Geometry>(glyph), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Paint(icon, LineIcon.StrokeProperty, ink);
        square.Child = icon;
        row.Children.Add(square);

        var name = Text(c.Name, 13, FontWeight.SemiBold, "T.Text");
        var sub = Text(c.Sub, 11.5, c.Live ? FontWeight.SemiBold : FontWeight.Normal, c.Live ? "T.AccentInk" : "T.TextSoft");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        var right = Text(c.Right, 11.5, c.Live ? FontWeight.Bold : FontWeight.Normal, c.Live ? "T.Muted" : "T.TextSoft");
        right.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }
}
