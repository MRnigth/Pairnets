using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// Draws the small branches of the tray panel: from the nest down to each online computer (a line, the speed on it, the
/// computer's icon, name and status) and the one red row for the computers that can't be reached. It only draws a
/// <see cref="TrayPanelModel"/>; the Mac/Linux app draws the same.
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

    private static Line Dash(string brushKey, bool dashed, double width = double.NaN)
    {
        var line = new Line { X1 = 0, X2 = 1, Y1 = 0, Y2 = 0, Stretch = Stretch.Fill, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, Width = width };
        line.SetResourceReference(Shape.StrokeProperty, brushKey);
        if (dashed)
            line.StrokeDashArray = new DoubleCollection { 2.5, 2 };
        return line;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    /// <summary>The 108-wide cell with the vertical stub on its left, which ends halfway on the last row.</summary>
    private static Grid Stub(bool elbow)
    {
        var cell = new Grid { Width = LineCell, Height = 24 };
        var stub = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Height = elbow ? 13 : 24 };
        stub.SetResourceReference(Border.BackgroundProperty, "T.Line");
        cell.Children.Add(stub);
        return cell;
    }

    private static Border Pill(string text)
    {
        var pill = new Border { Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
        var label = Text(text, 11, FontWeights.ExtraBold, "T.AccentInk");
        label.TextTrimming = TextTrimming.None;
        pill.Child = label;
        return pill;
    }

    private static Grid Row(TrayBranch b, bool elbow)
    {
        var row = new Grid { Height = 24, ToolTip = b.IsThis ? b.Name + " (this computer)" : b.Name + ": " + b.Side };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The line from the nest
        var cell = Stub(elbow);
        var inner = new Grid { Margin = new Thickness(2, 0, 0, 0) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        var key = LineKey(b.Kind);
        var first = Dash(key, b.Dashed);
        first.Margin = new Thickness(0, 0, b.HasPill || b.HasWord ? 4 : 0, 0);
        inner.Children.Add(first);
        if (b.HasPill || b.HasWord)
        {
            UIElement middle = b.HasPill ? Pill(b.Pill!) : Text(b.Word!, 11, FontWeights.Bold, WordKey(b.Kind));
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
        var square = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8) };
        square.SetResourceReference(Border.BackgroundProperty, b.Dot is NestDot.Off or NestDot.Bad ? "T.ChipDim" : "T.Chip");
        var icon = new LineIcon { Width = 13, Height = 13, StrokeThickness = 2, Data = DeviceIconArt.For(b.Icon) };
        icon.SetResourceReference(LineIcon.StrokeProperty, b.Dot is NestDot.Off or NestDot.Bad ? "T.TextSoft" : "T.Text");
        var dot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
        dot.SetResourceReference(Shape.StrokeProperty, "T.Card");
        dot.SetResourceReference(Shape.FillProperty, DotKey(b.Dot));
        var picture = new Grid { Width = 24, Height = 24, Margin = new Thickness(8, 0, 8, 0) };
        picture.Children.Add(square);
        picture.Children.Add(icon);
        picture.Children.Add(dot);
        Grid.SetColumn(picture, 1);
        row.Children.Add(picture);

        var name = Text(b.Name, 12.5, FontWeights.ExtraBold, "T.Text");
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var side = Text(b.Side, 11.5, b.SideInk == TrayInk.Muted ? FontWeights.Normal : FontWeights.SemiBold, InkKey(b.SideInk));
        side.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(side, 3);
        row.Children.Add(side);
        System.Windows.Automation.AutomationProperties.SetName(row, b.IsThis ? b.Name + ", this computer, " + (b.Word ?? b.Pill ?? "") : b.Name + ", " + b.Side);
        return row;
    }

    private static Grid Unreachable(TrayUnreachable gone)
    {
        var row = new Grid { Height = 24, ToolTip = gone.Tooltip };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A dashed red line with a red cross on it; the vertical stub stops at the line
        var cell = Stub(elbow: true);
        var inner = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        var first = Dash("T.Bad", dashed: true);
        first.Margin = new Thickness(0, 0, 3, 0);
        inner.Children.Add(first);
        var cross = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center };
        cross.SetResourceReference(Border.BackgroundProperty, "T.BadPill");
        cross.SetResourceReference(Border.BorderBrushProperty, "T.Bad");
        var x = new LineIcon { Width = 9, Height = 9, StrokeThickness = 3.2, Data = Visuals.Resource<Geometry>("I.X") };
        x.SetResourceReference(LineIcon.StrokeProperty, "T.Bad");
        cross.Child = x;
        System.Windows.Automation.AutomationProperties.SetName(cross, "Can't reach them");
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
            dash.SetResourceReference(Border.BackgroundProperty, "T.ChipDim");
            dash.SetResourceReference(Border.BorderBrushProperty, "T.Bad");
            var glyph = new LineIcon { Width = 10, Height = 10, StrokeThickness = 2.2, Data = DeviceIconArt.For(gone.Icons[i]) };
            glyph.SetResourceReference(LineIcon.StrokeProperty, "T.TextSoft");
            dash.Child = glyph;
            pile.Children.Add(dash);
        }
        Grid.SetColumn(pile, 1);
        row.Children.Add(pile);

        var text = Text(gone.Text, 12.5, FontWeights.Bold, "T.BadText");
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(row, gone.Tooltip);
        return row;
    }
}
