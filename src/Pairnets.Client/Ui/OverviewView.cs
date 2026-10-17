using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Overview page: the heading card for each of the nine states with the picture of the nest and its computers, the
/// "needs you" strip, and the cards below (Moving now, Latest changes, details). It only draws an <see cref="OverviewModel"/>
/// and a <see cref="NestMapModel"/>; the Mac/Linux app draws the same two. The pieces that change often are rebuilt only when
/// their words change, so the lines in the picture keep moving.
/// </summary>
public sealed class OverviewView : UserControl
{
    private readonly StackPanel _page = new();
    private readonly Border _heroCard = new();
    private readonly Border _heroTop = new();
    private readonly Border _heroDivider = new();
    private readonly HubView _hub = new();
    private readonly Border _needs = new();
    private readonly TextBlock _needsTitle = new();
    private readonly TextBlock _needsDetail = new();
    private readonly Border _lower = new();
    private readonly HashSet<string> _expanded = [];
    private readonly HashSet<string> _collapsed = [];
    private string _heroSignature = string.Empty;
    private string _lowerSignature = string.Empty;
    private FrameworkElement? _pulse;

    public OverviewView()
    {
        _page.Margin = new Thickness(28, 18, 28, 24);

        _heroCard.CornerRadius = new CornerRadius(20);
        _heroCard.BorderThickness = new Thickness(1);
        _heroCard.Padding = new Thickness(24, 22, 24, 22);
        _heroCard.SetResourceReference(Border.BackgroundProperty, "T.Card");
        var heroStack = new StackPanel();
        heroStack.Children.Add(_heroTop);
        _heroDivider.Height = 1;
        _heroDivider.Margin = new Thickness(0, 16, 0, 16);
        _heroDivider.SetResourceReference(Border.BackgroundProperty, "T.Border");
        heroStack.Children.Add(_heroDivider);
        _hub.HorizontalAlignment = HorizontalAlignment.Left;
        _hub.MoreClicked += () => MoreComputers?.Invoke();
        var hubHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        hubHost.Children.Add(_hub);
        heroStack.Children.Add(hubHost);
        _heroCard.Child = heroStack;
        _page.Children.Add(_heroCard);

        BuildNeeds();
        _page.Children.Add(_needs);

        _lower.Margin = new Thickness(0, 16, 0, 0);
        _page.Children.Add(_lower);
        Content = _page;
    }

    /// <summary>A button of the page was pressed.</summary>
    public event Action<OverviewAction>? Act;

    /// <summary>"See all in Activity".</summary>
    public event Action? SeeAllActivity;

    /// <summary>"+N more" in the picture: the Devices page opens.</summary>
    public event Action? MoreComputers;

    /// <summary>The model last drawn (the checks read it).</summary>
    public OverviewModel? Model { get; private set; }

    /// <summary>Show the speed on the lines of the picture (Settings: "Show speed on the map").</summary>
    public bool ShowSpeed
    {
        get => _hub.ShowSpeed;
        set => _hub.ShowSpeed = value;
    }

    /// <summary>Stops the breathing icon (nothing moves while the window sits in the tray).</summary>
    public void StopMotion()
    {
        if (_pulse is not null)
            Motion.Pulse(_pulse, false);
    }

    internal string TitleText => Model?.Title ?? string.Empty;

    internal string OtherText => _hub.OtherText;

    internal Geometry? ThisIconData => _hub.ThisIconData;

    internal Geometry? OtherIconData => _hub.OtherIconData;

    internal bool ThisFlowing => _hub.ThisFlowing;

    internal bool NeedsVisible => _needs.Visibility == Visibility.Visible;

    internal bool MovingVisible => Model is { ShowsMoving: true };

    internal int MovingRows => Model?.Moving.Count ?? 0;

    public void Show(OverviewModel m, NestMapModel map, string? thisName)
    {
        Model = m;
        _hub.Show(map, thisName);

        var hero = HeroSignature(m);
        _heroCard.SetResourceReference(Border.BorderBrushProperty, BorderKey(m.Border));
        if (hero != _heroSignature)
        {
            _heroSignature = hero;
            _heroTop.Child = BuildHero(m);
        }

        var needs = m.NeedsTitle is not null && !(m.State == NestMapState.Stopped && m.DeleteBox is not null);
        _needs.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        _needsTitle.Text = m.NeedsTitle ?? string.Empty;
        _needsDetail.Text = m.NeedsDetail;

        var lower = LowerSignature(m);
        if (lower != _lowerSignature)
        {
            _lowerSignature = lower;
            _lower.Child = BuildLower(m);
        }
    }

    // ------------------------------------------------------------------ colours and small parts

    private static string Ink(OverviewTone tone) => tone switch
    {
        OverviewTone.Accent => "T.AccentInk",
        OverviewTone.Ok => "T.OkText",
        OverviewTone.Warn => "T.WarnText",
        OverviewTone.Bad => "T.BadText",
        _ => "T.Muted",
    };

    private static string Soft(OverviewTone tone) => tone switch
    {
        OverviewTone.Accent => "T.AccentSoft",
        OverviewTone.Ok => "T.OkPill",
        OverviewTone.Warn => "T.WarnPill",
        OverviewTone.Bad => "T.BadPill",
        _ => "T.Chip",
    };

    private static string BorderKey(OverviewTone tone) => tone switch
    {
        OverviewTone.Warn => "T.WarnBorder",
        OverviewTone.Bad => "T.BadBorder",
        _ => "T.Border",
    };

    private static TextBlock Txt(string text, double size = 14, FontWeight? weight = null, string fg = "T.Text", bool wrap = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
        }
        else
        {
            block.TextWrapping = TextWrapping.NoWrap;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        block.SetResourceReference(TextBlock.ForegroundProperty, fg);
        return block;
    }

    /// <summary>A 24 by 24 line icon drawn at <paramref name="size"/> (a solid one where the shape says so).</summary>
    internal static FrameworkElement Glyph(OverviewIcon icon, double size, string? stroke, double thickness = 1.9)
    {
        var (data, filled) = OverviewIconShapes.Of(icon);
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        if (stroke is null)
        {
            path.Stroke = Brushes.White;
            if (filled)
                path.Fill = Brushes.White;
        }
        else
        {
            path.SetResourceReference(Shape.StrokeProperty, stroke);
            if (filled)
                path.SetResourceReference(Shape.FillProperty, stroke);
        }
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Border Square(OverviewIcon icon, double box, double glyph, double radius, string bg, string ink, double thickness = 1.9)
    {
        var square = new Border { Width = box, Height = box, CornerRadius = new CornerRadius(radius), Child = Glyph(icon, glyph, ink, thickness) };
        square.SetResourceReference(Border.BackgroundProperty, bg);
        return square;
    }

    private static Border Pill(string text, OverviewTone tone, double height = 24, double size = 12)
    {
        var pill = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, Soft(tone));
        var label = Txt(text, size, FontWeights.Bold, Ink(tone));
        label.VerticalAlignment = VerticalAlignment.Center;
        pill.Child = label;
        return pill;
    }

    private static Border Rule(double top = 0, double bottom = 0)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, top, 0, bottom) };
        line.SetResourceReference(Border.BackgroundProperty, "T.Border");
        return line;
    }

    private static ProgressBar Bar(double value, double height = 8)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(value, 0, 100), Height = height, VerticalAlignment = VerticalAlignment.Center };
        bar.Style = (Style)Application.Current.FindResource(height < 8 ? "ThinProgress" : "RoundProgress");
        bar.Height = height;
        return bar;
    }

    private Button MakeButton(OverviewButton b)
    {
        var button = new Button { Cursor = System.Windows.Input.Cursors.Hand };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        string ink;
        switch (b.Kind)
        {
            case OverviewButtonKind.Primary:
                button.Style = (Style)FindResource("AccentButton");
                ink = null!;
                break;
            case OverviewButtonKind.Warn:
                button.Style = (Style)FindResource("AccentButton");
                button.SetResourceReference(Control.BackgroundProperty, "T.NeedsActive");
                button.SetResourceReference(Control.TagProperty, "T.NeedsActive");
                button.SetResourceReference(Control.ForegroundProperty, "T.NeedsActiveText");
                ink = "T.NeedsActiveText";
                break;
            case OverviewButtonKind.Link:
                button.Style = (Style)FindResource("LinkButton");
                button.SetResourceReference(Control.ForegroundProperty, "T.AccentInk");
                ink = "T.AccentInk";
                break;
            default:
                button.Style = (Style)FindResource("SubtleButton");
                ink = "T.Text";
                break;
        }
        if (b.Kind != OverviewButtonKind.Link)
        {
            button.MinHeight = b.Large ? (b.Action == OverviewAction.TogglePause ? 50 : 44) : 40;
            button.Padding = new Thickness(b.Large ? (b.Action == OverviewAction.TogglePause ? 26 : 20) : 16, 0, b.Large ? 26 : 16, 0);
            if (b.Large && b.Action == OverviewAction.TogglePause)
                button.FontSize = 16;
            else if (b.Large)
                button.FontSize = 14;
            if (b.Kind == OverviewButtonKind.Primary)
                button.FontWeight = FontWeights.Bold;
        }
        if (b.Icon != OverviewIcon.None)
        {
            var icon = Glyph(b.Icon, b.Large ? (b.Action == OverviewAction.TogglePause ? 18 : 17) : 16, b.Kind == OverviewButtonKind.Primary ? null : ink, 2.1);
            icon.Margin = new Thickness(0, 0, b.Large ? 9 : 8, 0);
            content.Children.Add(icon);
        }
        content.Children.Add(new TextBlock { Text = b.Label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        var action = b.Action;
        button.Click += (_, _) => Act?.Invoke(action);
        System.Windows.Automation.AutomationProperties.SetName(button, b.Label);
        return button;
    }

    // ------------------------------------------------------------------ the heading

    private static string HeroSignature(OverviewModel m) => string.Join('\u001f',
        m.State, m.Border, m.IsSyncingBar, m.Chip, m.ChipTone, m.Glyph, m.GlyphTone, m.Title, m.Badge, m.Detail,
        m.Progress, m.HeadButton, m.WaitBox, m.DeleteBox is null ? null : string.Join('|', m.DeleteBox.Files.Select(f => f.ToString())) + m.DeleteBox.CountText + m.DeleteBox.More,
        m.FolderBox, m.Note, string.Join('|', m.Checks), string.Join('|', m.Infos), string.Join('|', m.Tiles), string.Join('|', m.Buttons), m.ButtonNote);

    private UIElement BuildHero(OverviewModel m)
    {
        _pulse = null;
        return m.IsSyncingBar ? BuildSyncingHero(m) : BuildStateHero(m);
    }

    private UIElement BuildSyncingHero(OverviewModel m)
    {
        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chip = new Border { Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, Soft(m.ChipTone));
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, m.ChipTone == OverviewTone.Warn ? "T.Warn" : "T.Accent");
        chipRow.Children.Add(dot);
        var chipText = Txt(m.Chip, 12, FontWeights.Bold, Ink(m.ChipTone));
        chipText.VerticalAlignment = VerticalAlignment.Center;
        chipRow.Children.Add(chipText);
        chip.Child = chipRow;
        top.Children.Add(chip);

        var title = Txt(m.Title, 18, FontWeights.Bold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        top.Children.Add(title);

        var percent = Txt(m.Progress?.Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty, 30, FontWeights.ExtraBold);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(percent, 2);
        top.Children.Add(percent);
        stack.Children.Add(top);

        var bar = Bar(m.Progress?.Percent ?? 0);
        bar.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(bar);

        var texts = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Txt(m.Progress?.Left ?? string.Empty, 13, null, "T.Muted");
        var right = Txt(m.Progress?.Right ?? string.Empty, 13, null, m.Progress?.RightWarn == true ? "T.WarnText" : "T.Muted");
        right.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(right, 1);
        texts.Children.Add(left);
        texts.Children.Add(right);
        stack.Children.Add(texts);
        return stack;
    }

    private UIElement BuildStateHero(OverviewModel m)
    {
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var circle = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        circle.SetResourceReference(Border.BackgroundProperty, Soft(m.GlyphTone));
        var glyph = Glyph(m.Glyph, m.Glyph == OverviewIcon.Pause ? 24 : 26, Ink(m.GlyphTone), m.Glyph == OverviewIcon.Check ? 2.6 : m.Glyph == OverviewIcon.Pause ? 2.6 : 2);
        circle.Child = glyph;
        head.Children.Add(circle);
        if (m.Pulses)
        {
            _pulse = circle;
            Motion.Pulse(circle, true);
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Txt(m.Title, 22, FontWeights.ExtraBold));
        if (m.Badge is { } badge)
        {
            var pill = Pill(badge, m.BadgeTone);
            pill.Margin = new Thickness(10, 0, 0, 0);
            titleRow.Children.Add(pill);
        }
        titleRow.Children[0].SetValue(TextBlock.TextTrimmingProperty, TextTrimming.None);
        texts.Children.Add(titleRow);
        if (m.Detail.Length > 0)
            texts.Children.Add(Txt(m.Detail, 14, null, "T.Muted", wrap: true));
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);

        if (m.HeadButton is { } headButton)
        {
            var button = MakeButton(headButton);
            button.Margin = new Thickness(16, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 2);
            head.Children.Add(button);
        }
        stack.Children.Add(head);

        var indent = m.State is NestMapState.SignIn or NestMapState.Paused ? 68.0 : 0.0;

        if (m.WaitBox is { } wait)
            stack.Children.Add(Space(BuildWaitBox(wait)));
        if (m.DeleteBox is { } delete)
            stack.Children.Add(Space(BuildDeleteBox(delete)));
        if (m.FolderBox is { } folder)
            stack.Children.Add(Space(BuildFolderBox(folder)));
        if (m.Note is { } note)
            stack.Children.Add(Space(BuildNote(note, m.State == NestMapState.SignedOut ? OverviewTone.Bad : OverviewTone.Neutral)));
        if (m.Checks.Count > 0)
            stack.Children.Add(Space(BuildInfoRow(m.Checks, indent, check: true)));
        if (m.Infos.Count > 0)
            stack.Children.Add(Space(BuildInfoRow(m.Infos, indent, check: false)));
        if (m.Tiles.Count > 0)
            stack.Children.Add(Space(BuildTiles(m.Tiles)));
        if (m.Buttons.Count > 0)
            stack.Children.Add(Space(BuildButtons(m, indent)));
        return stack;
    }

    private static FrameworkElement Space(FrameworkElement element, double top = 16)
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    private FrameworkElement BuildWaitBox(OverviewWaitBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
        card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        var stack = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Square(OverviewIcon.Computer, 32, 16, 10, "T.AccentSoft", "T.AccentInk");
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(box.Title, 14, FontWeights.Bold));
        texts.Children.Add(Txt(box.Sub, 12, null, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var pct = Txt(box.Percent.ToString(CultureInfo.InvariantCulture) + "%", 24, FontWeights.ExtraBold);
        pct.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pct, 2);
        row.Children.Add(pct);
        stack.Children.Add(row);
        var bar = Bar(box.Percent);
        bar.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(bar);
        if (box.Skip is { } skip)
        {
            var link = MakeButton(skip);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Margin = new Thickness(-6, 8, 0, -6);
            stack.Children.Add(link);
        }
        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildDeleteBox(OverviewDeleteBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "T.WarnPill");
        card.SetResourceReference(Border.BorderBrushProperty, "T.WarnBorder");
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Txt(box.Heading, 12, FontWeights.Bold, "T.WarnText"));
        var count = Txt(box.CountText, 12, null, "T.Muted");
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        stack.Children.Add(head);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var file in box.Files)
        {
            var item = new Grid { Margin = new Thickness(0, 0, 20, 8) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Square(file.Icon, 30, 15, 9, "T.Card", "T.Muted");
            icon.Margin = new Thickness(0, 0, 10, 0);
            item.Children.Add(icon);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Txt(file.Name, 13, FontWeights.SemiBold));
            texts.Children.Add(Txt(file.Sub, 12, null, "T.TextSoft"));
            Grid.SetColumn(texts, 1);
            item.Children.Add(texts);
            grid.Children.Add(item);
        }
        stack.Children.Add(grid);
        if (box.More is { } more)
            stack.Children.Add(Txt(more, 12, null, "T.Muted"));
        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildFolderBox(OverviewFolderBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
        card.SetResourceReference(Border.BorderBrushProperty, "T.WarnBorder");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Square(OverviewIcon.Folder, 36, 18, 11, "T.Chip", "T.TextSoft");
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(box.Path, 14, FontWeights.Bold));
        texts.Children.Add(Txt(box.Sub, 12, null, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var badge = Pill(box.Badge, OverviewTone.Warn);
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);
        card.Child = row;
        // A dashed outline: the border is drawn by a rectangle behind the content.
        var host = new Grid();
        var dashed = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1, StrokeDashArray = new DoubleCollection([4, 3]), IsHitTestVisible = false };
        dashed.SetResourceReference(Shape.StrokeProperty, "T.WarnBorder");
        card.BorderThickness = new Thickness(0);
        host.Children.Add(dashed);
        host.Children.Add(card);
        return host;
    }

    private FrameworkElement BuildNote(string text, OverviewTone tone)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
        card.SetResourceReference(Border.BackgroundProperty, tone == OverviewTone.Bad ? "T.BadPill" : "T.Chip");
        card.SetResourceReference(Border.BorderBrushProperty, tone == OverviewTone.Bad ? "T.BadBorder" : "T.Border");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(OverviewIcon.Info, 18, Ink(tone), 2);
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var label = Txt(text, 13, null, "T.Text", wrap: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        card.Child = row;
        return card;
    }

    private static FrameworkElement BuildInfoRow(IReadOnlyList<OverviewInfo> items, double indent, bool check)
    {
        var panel = new WrapPanel { Margin = new Thickness(indent, 0, 0, 0) };
        foreach (var item in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 4) };
            if (check)
            {
                var ok = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0), Child = Glyph(OverviewIcon.Check, 12, "T.OkText", 3) };
                ok.SetResourceReference(Border.BackgroundProperty, "T.OkPill");
                row.Children.Add(ok);
            }
            else
            {
                var icon = Glyph(item.Icon, 16, "T.Muted", 1.9);
                icon.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(icon);
            }
            var text = Txt(item.Text, 13, null, "T.Muted");
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static FrameworkElement BuildTiles(IReadOnlyList<OverviewTile> tiles)
    {
        var grid = new UniformGrid { Columns = tiles.Count };
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(i == 0 ? 0 : 6, 0, i == tiles.Count - 1 ? 0 : 6, 0) };
            card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
            var stack = new StackPanel();
            stack.Children.Add(Txt(tile.Label, 12, null, "T.TextSoft"));
            stack.Children.Add(Txt(tile.Value, 15, FontWeights.Bold));
            if (tile.Sub is { } sub)
                stack.Children.Add(Txt(sub, 12, null, "T.Muted"));
            card.Child = stack;
            grid.Children.Add(card);
        }
        return grid;
    }

    private FrameworkElement BuildButtons(OverviewModel m, double indent)
    {
        var row = new Grid { Margin = new Thickness(indent, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in m.Buttons)
        {
            var button = MakeButton(b);
            button.Margin = new Thickness(0, 0, 10, 0);
            buttons.Children.Add(button);
        }
        row.Children.Add(buttons);
        if (m.ButtonNote is { } note)
        {
            var text = Txt(note, 12, null, "T.TextSoft");
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = indent > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            text.TextAlignment = indent > 0 ? TextAlignment.Left : TextAlignment.Right;
            if (indent > 0)
                text.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
        }
        return row;
    }

    // ------------------------------------------------------------------ the strip

    private void BuildNeeds()
    {
        _needs.CornerRadius = new CornerRadius(16);
        _needs.BorderThickness = new Thickness(1);
        _needs.Padding = new Thickness(16, 10, 10, 10);
        _needs.Margin = new Thickness(0, 16, 0, 0);
        _needs.Visibility = Visibility.Collapsed;
        _needs.SetResourceReference(Border.BackgroundProperty, "T.WarnPill");
        _needs.SetResourceReference(Border.BorderBrushProperty, "T.WarnBorder");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(OverviewIcon.Warning, 18, "T.WarnText", 2);
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        _needsTitle.FontWeight = FontWeights.Bold;
        _needsTitle.VerticalAlignment = VerticalAlignment.Center;
        _needsTitle.Margin = new Thickness(0, 0, 12, 0);
        _needsTitle.SetResourceReference(TextBlock.ForegroundProperty, "T.WarnText");
        Grid.SetColumn(_needsTitle, 1);
        row.Children.Add(_needsTitle);
        _needsDetail.VerticalAlignment = VerticalAlignment.Center;
        _needsDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        _needsDetail.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
        Grid.SetColumn(_needsDetail, 2);
        row.Children.Add(_needsDetail);
        var review = new Button { Content = "Review", Height = 34, MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 0, 0) };
        review.Style = (Style)Application.Current.FindResource("SubtleButton");
        review.MinHeight = 34;
        review.SetResourceReference(Control.BorderBrushProperty, "T.WarnBorder");
        review.Click += (_, _) => Act?.Invoke(OverviewAction.Review);
        Grid.SetColumn(review, 3);
        row.Children.Add(review);
        _needs.Child = row;
    }

    // ------------------------------------------------------------------ the cards below

    private static string LowerSignature(OverviewModel m) => string.Join('\u001f',
        m.Lower, m.MovingCount, string.Join('|', m.Moving.Select(r => r + string.Join(',', r.Files.Select(f => f.ToString())))),
        string.Join('|', m.Changes), string.Join('|', m.Facts));

    private UIElement BuildLower(OverviewModel m) => m.Lower switch
    {
        OverviewLower.Moving => BuildMoving(m),
        OverviewLower.ChangesAndFacts => BuildChangesAndFacts(m),
        _ => BuildStrip(m),
    };

    private static Border Card(UIElement child, Thickness padding)
    {
        var card = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = padding, Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "T.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        return card;
    }

    private UIElement BuildMoving(OverviewModel m)
    {
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Txt("Moving now", 16, FontWeights.ExtraBold));
        var count = Txt(m.MovingCount, 12, null, "T.TextSoft");
        count.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        stack.Children.Add(head);

        if (m.Moving.Count == 0)
        {
            var empty = Txt(m.Flow == OverviewFlow.Checking ? "Pairnets is looking through your folder. Files show up here when they start moving." : "Files show up here as they start moving.", 13, null, "T.Muted", wrap: true);
            empty.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(empty);
            return Card(stack, new Thickness(22, 18, 22, 18));
        }

        var firstFolder = true;
        var sawFolder = false;
        foreach (var row in m.Moving)
        {
            if (!row.IsFolder && sawFolder)
            {
                stack.Children.Add(Rule(12, 0));
                sawFolder = false;
            }
            var open = row.IsFolder && IsOpen(row.Key, firstFolder);
            if (row.IsFolder)
                firstFolder = false;
            stack.Children.Add(BuildMovingRow(row, open));
            if (row.IsFolder)
            {
                sawFolder = true;
                if (open)
                    foreach (var file in row.Files)
                        stack.Children.Add(BuildMovingFile(file));
            }
        }
        return Card(stack, new Thickness(22, 18, 22, 18));
    }

    /// <summary>The first folder of the list starts open; a click opens or closes any of them.</summary>
    private bool IsOpen(string key, bool first) => _expanded.Contains(key) || (first && !_collapsed.Contains(key) && !_expanded.Contains(key));

    private FrameworkElement BuildMovingRow(OverviewMovingRow row, bool open)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Square(row.Icon, 36, 18, 11, "T.Chip", row.IsFolder ? "T.Folder" : "T.Muted", row.IsFolder ? 1.4 : 1.8);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(row.Title, row.IsFolder ? 14 : 13, row.IsFolder ? FontWeights.Bold : FontWeights.SemiBold));
        if (row.Sub.Length > 0)
            texts.Children.Add(Txt(row.Sub, 12, null, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (row.IsFolder)
        {
            if (open)
            {
                var chevron = Glyph(OverviewIcon.None, 16, "T.Muted");
                chevron = ChevronGlyph(true);
                Grid.SetColumn(chevron, 3);
                grid.Children.Add(chevron);
            }
            else
            {
                AddBar(grid, 2, row.Percent, row.PercentText);
                var chevron = ChevronGlyph(false);
                chevron.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(chevron, 4);
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(chevron);
            }
            grid.Cursor = System.Windows.Input.Cursors.Hand;
            var key = row.Key;
            grid.MouseLeftButtonUp += (_, _) =>
            {
                if (open)
                {
                    _expanded.Remove(key);
                    _collapsed.Add(key);
                }
                else
                {
                    _collapsed.Remove(key);
                    _expanded.Add(key);
                }
                _lowerSignature = string.Empty;
                if (Model is { } model)
                {
                    _lowerSignature = LowerSignature(model);
                    _lower.Child = BuildLower(model);
                }
            };
        }
        else
        {
            AddBar(grid, 2, row.Percent, row.PercentText);
        }
        return grid;
    }

    private static FrameworkElement ChevronGlyph(bool up)
    {
        var chevron = Glyph(OverviewIcon.None, 16, "T.Muted", 2.2);
        if (chevron is Viewbox { Child: Canvas canvas })
        {
            canvas.Children.Clear();
            var path = new Path { Data = Geometry.Parse(up ? "m6 15 6-6 6 6" : "m6 9 6 6 6-6"), StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetResourceReference(Shape.StrokeProperty, "T.Muted");
            canvas.Children.Add(path);
        }
        chevron.VerticalAlignment = VerticalAlignment.Center;
        return chevron;
    }

    private static void AddBar(Grid grid, int barColumn, int? percent, string text)
    {
        var bar = Bar(percent ?? 0, 6);
        bar.Width = 180;
        bar.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(bar, barColumn);
        grid.Children.Add(bar);
        var label = Txt(text, 12, FontWeights.Bold, "T.Muted");
        label.Width = 36;
        label.TextAlignment = TextAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(label, barColumn + 1);
        grid.Children.Add(label);
    }

    private static FrameworkElement BuildMovingFile(OverviewMovingFile file)
    {
        var grid = new Grid { Margin = new Thickness(48, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Txt(file.Name, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        if (file.Percent is { } p)
            AddBar(grid, 1, p, p.ToString(CultureInfo.InvariantCulture) + "%");
        else
        {
            var wait = Txt(file.StateText, 12, null, "T.TextSoft");
            wait.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(wait, 2);
            grid.Children.Add(wait);
        }
        return grid;
    }

    private UIElement BuildChangesAndFacts(OverviewModel m)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Txt("Latest changes", 16, FontWeights.ExtraBold));
        var see = new Button { Content = "See all in Activity", Style = (Style)Application.Current.FindResource("LinkButton"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -3) };
        see.SetResourceReference(Control.ForegroundProperty, "T.AccentInk");
        see.FontWeight = FontWeights.Bold;
        see.Click += (_, _) => SeeAllActivity?.Invoke();
        Grid.SetColumn(see, 1);
        head.Children.Add(see);
        left.Children.Add(head);
        if (m.Changes.Count == 0)
        {
            var empty = Txt("Nothing has happened yet. Changes appear here as they sync.", 13, null, "T.Muted", wrap: true);
            empty.Margin = new Thickness(0, 12, 0, 0);
            left.Children.Add(empty);
        }
        foreach (var change in m.Changes)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Square(change.Icon, 34, 16, 11, "T.Chip", Ink(change.Tone), 2.2);
            icon.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(icon);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Txt(change.Name, 14, FontWeights.SemiBold));
            texts.Children.Add(Txt(change.Sub, 12, null, "T.TextSoft"));
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var time = Txt(change.Time, 12, null, "T.TextSoft");
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 2);
            row.Children.Add(time);
            left.Children.Add(row);
        }
        grid.Children.Add(Card(left, new Thickness(22, 18, 22, 18)));

        var right = new Grid();
        for (var i = 0; i < m.Facts.Count; i++)
        {
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 66 });
            var fact = BuildFact(m.Facts[i], i > 0);
            Grid.SetRow(fact, i);
            right.Children.Add(fact);
        }
        var factCard = Card(right, new Thickness(0));
        factCard.Padding = new Thickness(0);
        Grid.SetColumn(factCard, 2);
        grid.Children.Add(factCard);
        return grid;
    }

    private FrameworkElement BuildFact(OverviewFact fact, bool divider)
    {
        // The link sits under the value, not beside it: beside it, a narrow window left the label and the value about
        // 40 px ("Nes… 1.…").
        var row = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var isFolder = fact.Icon is OverviewIcon.FolderFill;
        var icon = Square(fact.Icon, 34, 16, 11, "T.Chip", isFolder ? "T.Folder" : "T.Muted", isFolder ? 1.4 : 1.9);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(fact.Label, 12, null, "T.TextSoft"));
        texts.Children.Add(Txt(fact.Value, 14, FontWeights.Bold, fact.Tone == OverviewTone.Neutral ? "T.Text" : Ink(fact.Tone)));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (fact.LinkText is { } link)
        {
            var button = new Button { Content = link, Style = (Style)Application.Current.FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.Bold, Margin = new Thickness(-6, 1, 0, -3) };
            button.SetResourceReference(Control.ForegroundProperty, "T.AccentInk");
            var action = fact.LinkAction;
            button.Click += (_, _) => Act?.Invoke(action);
            texts.Children.Add(button);
        }
        if (!divider)
            return row;
        var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        wrap.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        row.Margin = new Thickness(18, 0, 18, 0);
        return wrap;
    }

    private UIElement BuildStrip(OverviewModel m)
    {
        var grid = new Grid();
        for (var i = 0; i < m.Facts.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new Border { BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Padding = new Thickness(0, 14, 0, 14) };
            cell.SetResourceReference(Border.BorderBrushProperty, "T.Border");
            cell.Child = BuildFact(m.Facts[i], false);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return Card(grid, new Thickness(0));
    }
}

