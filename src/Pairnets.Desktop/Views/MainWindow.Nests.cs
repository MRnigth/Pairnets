using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Several nests (docs/DECISIONS.md, "Several nests at once"): the nest list at the top of the left side ("All nests", one
/// row per nest, "Add a nest"), the picked nest's name over the page title, and the All nests page with a card per nest.
/// It only draws a <see cref="NestListModel"/>; the Windows app draws the same model. With one nest none of it shows.
/// </summary>
public partial class MainWindow
{
    private NestListModel? _nests;

    /// <summary>The Overview shows "All nests" instead: several nests, none picked.</summary>
    private bool ShowsAllNests => _nests is { Visible: true, AllSelected: true };

    /// <summary>Draws the nest list (the controller calls it with every redraw).</summary>
    public void ShowNests(NestListModel nests)
    {
        var before = _nests;
        _nests = nests;
        NestListPanel.IsVisible = nests.Visible;
        ComputersLabel.Text = nests.Visible && nests.Selected is { } picked ? "IN " + picked.Title.ToUpperInvariant() : "YOUR COMPUTERS";
        if (before is null || !before.Rows.SequenceEqual(nests.Rows) || before.AllSub != nests.AllSub || before.AllSelected != nests.AllSelected)
        {
            DrawNestRows(nests);
            DrawAllNests(nests);
        }
        PauseText.Text = nests.Visible ? (_status.Paused ? "Resume all" : "Pause all") : PauseText.Text;
        if (Page == MainPage.Overview && (before?.Visible != nests.Visible || before?.AllSelected != nests.AllSelected))
            ShowPage(MainPage.Overview); // between one nest's Overview and All nests
        else
            DrawPageNest();
    }

    /// <summary>The picked nest's name over the page title ("WORK NEST"), when there are several.</summary>
    private void DrawPageNest()
    {
        var name = _nests is { Visible: true, AllSelected: false, Selected: { } picked } && Page != MainPage.Settings ? picked.Title.ToUpperInvariant() : null;
        if (_nests is { Visible: true, AllSelected: true } && Page == MainPage.Overview)
            name = "THIS COMPUTER";
        PageNest.Text = name ?? string.Empty;
        PageNest.IsVisible = name is not null;
    }

    private void DrawNestRows(NestListModel m)
    {
        NestRows.Children.Clear();
        NestRows.Children.Add(NestListRow("All nests", m.AllSub, Visuals.Resource<Geometry>("I.Grid"), null, m.AllSelected, () => _actions?.SelectNest(null),
            "All nests, " + m.AllSub));
        foreach (var row in m.Rows)
        {
            var id = row.Id;
            NestRows.Children.Add(NestListRow(row.Title, row.Sub, Visuals.Resource<Geometry>("I.Nest"), DotKey(row.Dot), row.Selected && !m.AllSelected,
                () => _actions?.SelectNest(id), row.Tooltip, row.Tone));
        }
        var add = NestListRow("Add a nest", null, Visuals.Resource<Geometry>("I.Plus"), null, false, () => _actions?.AddNest(), "Add a nest", dashed: true);
        NestRows.Children.Add(add);
    }

    /// <summary>One row of the nest list: an icon square (with the nest's dot), the name and the line under it.</summary>
    private static Button NestListRow(string title, string? sub, Geometry? icon, string? dotKey, bool selected, Action onClick, string tip,
        SideTone tone = SideTone.Neutral, bool dashed = false)
    {
        var square = new Border();
        square.Classes.Add("IconSquare");
        if (selected)
            Paint(square, Border.BackgroundProperty, "T.Card");
        if (dashed)
        {
            square.Background = Brushes.Transparent;
            square.BorderThickness = new Thickness(1.5);
            Paint(square, Border.BorderBrushProperty, "T.Dash");
        }
        var glyph = new LineIcon { Width = 17, Height = 17, StrokeThickness = 1.8, Data = icon };
        Paint(glyph, LineIcon.StrokeProperty, selected ? "T.AccentInk" : dashed ? "T.Muted" : "T.Text");
        var picture = new Grid { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { square, glyph } };
        if (dotKey is not null)
        {
            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
            Paint(dot, Shape.StrokeProperty, selected ? "T.AccentSoft" : "T.Card");
            Paint(dot, Shape.FillProperty, dotKey);
            picture.Children.Add(dot);
        }
        var name = new TextBlock { Text = title, FontWeight = FontWeight.ExtraBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Paint(name, TextBlock.ForegroundProperty, selected ? "T.AccentInk" : dashed ? "T.Muted" : "T.Text");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (sub is not null)
        {
            var line = new TextBlock { Text = sub, FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            Paint(line, TextBlock.ForegroundProperty, InkKey(tone));
            text.Children.Add(line);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(text, 1);
        grid.Children.Add(picture);
        grid.Children.Add(text);
        var button = new Button { Content = grid, Margin = new Thickness(0, 2, 0, 0), Background = Brushes.Transparent };
        button.Classes.Add("siderow");
        if (selected)
            Paint(button, BackgroundProperty, "T.AccentSoft");
        SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ------------------------------------------------------------------ the All nests page

    private void DrawAllNests(NestListModel m)
    {
        AllNestsCards.Children.Clear();
        foreach (var row in m.Rows)
            AllNestsCards.Children.Add(NestCardOf(row));
        var add = new Button { Margin = new Thickness(0, 14, 0, 0), Background = Brushes.Transparent };
        add.Classes.Add("siderow");
        var plus = new LineIcon { Width = 16, Height = 16, StrokeThickness = 2.2, Data = Visuals.Resource<Geometry>("I.Plus") };
        Paint(plus, LineIcon.StrokeProperty, "T.AccentInk");
        var addTitle = new TextBlock { Text = "Add a nest", FontWeight = FontWeight.ExtraBold };
        var addSub = new TextBlock { Text = NestListModel.AddNestHint, FontSize = 12 };
        Paint(addSub, TextBlock.ForegroundProperty, "T.Muted");
        var addText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { addTitle, addSub } };
        var addBox = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(20), Padding = new Thickness(12, 8, 12, 8) };
        Paint(addBox, Border.BorderBrushProperty, "T.Dash");
        addBox.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Child = plus }, addText },
        };
        add.Content = addBox;
        SetTip(add, "Add a nest");
        add.Click += (_, _) => _actions?.AddNest();
        AllNestsCards.Children.Add(add);
    }

    /// <summary>One nest's card on the All nests page: its state, folder, this computer's name there, and Show.</summary>
    private Border NestCardOf(NestRow row)
    {
        var (badge, glyph, ink) = row.Icon switch
        {
            RunnerStatus.Idle => ("T.OkPill", "I.Check", "T.Ok"),
            RunnerStatus.Syncing => ("T.AccentSoft", "I.Sync", "T.Accent"),
            RunnerStatus.Error => ("T.BadPill", "I.X", "T.BadText"),
            RunnerStatus.Blocked or RunnerStatus.Paused => ("T.WarnPill", "I.Warning", "T.WarnText"),
            _ => ("T.Chip", "I.CloudOff", "T.Muted"),
        };
        var mark = new LineIcon { Width = 20, Height = 20, StrokeThickness = 2.4, Data = Visuals.Resource<Geometry>(glyph) };
        Paint(mark, LineIcon.StrokeProperty, ink);
        var circle = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 14, 0), Child = mark };
        Paint(circle, Border.BackgroundProperty, badge);
        var title = new TextBlock { Text = row.Title, FontSize = 18, FontWeight = FontWeight.ExtraBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock
        {
            Text = char.ToUpperInvariant(row.Text[0]) + row.Text[1..] + (row.AccountEmail is { } email ? " · signed in as " + AccountSignIn.MaskEmail(email) : string.Empty),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Paint(state, TextBlock.ForegroundProperty, "T.Muted");
        var show = new Button { Content = "Show", VerticalAlignment = VerticalAlignment.Center };
        show.Classes.Add("subtle");
        AutomationProperties.SetName(show, "Show " + row.Title);
        var id = row.Id;
        show.Click += (_, _) => _actions?.SelectNest(id);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, state } };
        Grid.SetColumn(words, 1);
        Grid.SetColumn(show, 2);
        head.Children.Add(circle);
        head.Children.Add(words);
        head.Children.Add(show);

        var body = new StackPanel { Children = { head } };
        if (row.Percent is { } percent && row.Icon == RunnerStatus.Syncing)
            body.Children.Add(new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = percent, Margin = new Thickness(0, 14, 0, 0) });
        var facts = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
        facts.Children.Add(Fact("Folder", row.Folder ?? "Not chosen yet"));
        facts.Children.Add(Fact("This computer is called", row.DeviceName));
        body.Children.Add(facts);
        var card = new Border { Padding = new Thickness(22, 20, 22, 20), Margin = new Thickness(0, 0, 0, 14), Child = body };
        card.Classes.Add("card");
        return card;
    }

    private static Border Fact(string label, string value)
    {
        var key = new TextBlock { Text = label, FontSize = 11.5 };
        Paint(key, TextBlock.ForegroundProperty, "T.Muted");
        var val = new TextBlock { Text = value, FontSize = 13.5, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(val, value);
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0), Child = new StackPanel { Children = { key, val } } };
        Paint(box, Border.BackgroundProperty, "T.Tile");
        return box;
    }
}
