using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Collections;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;
using Path = Avalonia.Controls.Shapes.Path;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The Overview page: the heading card for each of the nine states with the picture of the nest and its computers, the
/// "needs you" strip, and the cards below (Moving now, Latest changes, details). It only draws an <see cref="OverviewModel"/>
/// and a <see cref="NestMapModel"/>; the Windows app draws the same two. The pieces that change often are rebuilt only when
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
    private Control? _pulse;

    public OverviewView()
    {
        _page.Margin = new Thickness(28, 18, 28, 24);

        _heroCard.CornerRadius = new CornerRadius(20);
        _heroCard.BorderThickness = new Thickness(1);
        _heroCard.Padding = new Thickness(24, 22, 24, 22);
        Paint(_heroCard, Border.BackgroundProperty, "T.Card");
        var heroStack = new StackPanel();
        heroStack.Children.Add(_heroTop);
        _heroDivider.Height = 1;
        _heroDivider.Margin = new Thickness(0, 16, 0, 16);
        Paint(_heroDivider, Border.BackgroundProperty, "T.Border");
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

    internal string TitleText => Model?.Title ?? string.Empty;

    internal string OtherText => _hub.OtherText;

    internal bool ThisFlowing => _hub.ThisFlowing;

    internal bool NeedsVisible => _needs.IsVisible;

    internal bool MovingVisible => Model is { ShowsMoving: true };

    internal int MovingRows => Model?.Moving.Count ?? 0;

    internal bool Pulsing => _pulse?.Classes.Contains("pulse") == true;

    /// <summary>Stops the breathing icon (nothing moves while the window is hidden).</summary>
    public void StopMotion() => _pulse?.Classes.Remove("pulse");

    public void Show(OverviewModel m, NestMapModel map, string? thisName)
    {
        Model = m;
        _hub.Show(map, thisName);

        var hero = HeroSignature(m);
        Paint(_heroCard, Border.BorderBrushProperty, BorderKey(m.Border));
        if (hero != _heroSignature)
        {
            _heroSignature = hero;
            _heroTop.Child = BuildHero(m);
        }

        var needs = m.NeedsTitle is not null && !(m.State == NestMapState.Stopped && m.DeleteBox is not null);
        _needs.IsVisible = needs;
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

    private static void Paint(Control target, AvaloniaProperty property, string key) => target.Bind(property, target.GetResourceObservable(key));

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

    private static TextBlock Txt(string text, double size = 14, FontWeight weight = FontWeight.Normal, string fg = "T.Text", bool wrap = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
        }
        else
        {
            block.TextWrapping = TextWrapping.NoWrap;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        Paint(block, TextBlock.ForegroundProperty, fg);
        return block;
    }

    /// <summary>A 24 by 24 line icon drawn at <paramref name="size"/> (a solid one where the shape says so).</summary>
    internal static Control Glyph(OverviewIcon icon, double size, string? stroke, double thickness = 1.9)
    {
        var (data, filled) = OverviewIconShapes.Of(icon);
        var path = new Path
        {
            Data = data.Length == 0 ? null : Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
        };
        if (stroke is null)
        {
            path.Stroke = Brushes.White;
            if (filled)
                path.Fill = Brushes.White;
        }
        else
        {
            Paint(path, Shape.StrokeProperty, stroke);
            if (filled)
                Paint(path, Shape.FillProperty, stroke);
        }
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Border Square(OverviewIcon icon, double box, double glyph, double radius, string bg, string ink, double thickness = 1.9)
    {
        var square = new Border { Width = box, Height = box, CornerRadius = new CornerRadius(radius), Child = Glyph(icon, glyph, ink, thickness) };
        Paint(square, Border.BackgroundProperty, bg);
        return square;
    }

    private static Border Pill(string text, OverviewTone tone, double height = 24, double size = 12)
    {
        var pill = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(pill, Border.BackgroundProperty, Soft(tone));
        var label = Txt(text, size, FontWeight.Bold, Ink(tone));
        label.VerticalAlignment = VerticalAlignment.Center;
        pill.Child = label;
        return pill;
    }

    private static Border Rule(double top = 0, double bottom = 0)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, top, 0, bottom) };
        Paint(line, Border.BackgroundProperty, "T.Border");
        return line;
    }

    private static ProgressBar Bar(double value, double height = 8)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(value, 0, 100), Height = height, MinHeight = height, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(height / 2) };
        return bar;
    }

    private Button MakeButton(OverviewButton b)
    {
        var button = new Button { Cursor = new Cursor(StandardCursorType.Hand), VerticalContentAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        string? ink;
        switch (b.Kind)
        {
            case OverviewButtonKind.Primary:
                button.Classes.Add("accent");
                ink = null;
                break;
            case OverviewButtonKind.Warn:
                button.Classes.Add("amber");
                ink = "T.NeedsActiveText";
                break;
            case OverviewButtonKind.Link:
                button.Classes.Add("link");
                ink = "T.AccentInk";
                break;
            default:
                button.Classes.Add("subtle");
                ink = "T.Text";
                break;
        }
        var resume = b.Large && b.Action == OverviewAction.TogglePause;
        if (b.Kind != OverviewButtonKind.Link)
        {
            button.MinHeight = b.Large ? (resume ? 50 : 44) : 40;
            button.Padding = new Thickness(b.Large ? (resume ? 26 : 20) : 16, 0, b.Large ? (resume ? 26 : 20) : 16, 0);
            if (resume)
            {
                button.FontSize = 16;
                button.FontWeight = FontWeight.ExtraBold;
                button.CornerRadius = new CornerRadius(14);
            }
            else if (b.Large)
            {
                button.FontSize = 14;
            }
        }
        if (b.Icon != OverviewIcon.None)
        {
            var icon = Glyph(b.Icon, b.Large ? (resume ? 18 : 17) : 16, ink, 2.1);
            icon.Margin = new Thickness(0, 0, b.Large ? 9 : 8, 0);
            content.Children.Add(icon);
        }
        content.Children.Add(new TextBlock { Text = b.Label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        var action = b.Action;
        button.Click += HandlerFor(action);
        if (action == OverviewAction.CheckNestUpdate)
            button.Name = "VersionButton";
        AutomationProperties.SetName(button, b.Label);
        return button;
    }

    // ------------------------------------------------------------------ what the buttons do (one named handler each)

    private EventHandler<RoutedEventArgs> HandlerFor(OverviewAction action) => action switch
    {
        OverviewAction.Fix => OnFix,
        OverviewAction.SyncNow => OnSyncNow,
        OverviewAction.TogglePause => OnPause,
        OverviewAction.OpenFolder => OnOpenFolder,
        OverviewAction.DownloadNow => OnDownloadNow,
        OverviewAction.CheckNestUpdate => OnUpdateServer,
        OverviewAction.OpenAccount => OnOpenAccount,
        OverviewAction.SeeList => OnSeeList,
        OverviewAction.Review => OnReviewAttention,
        _ => OnOverviewButton,
    };

    private void OnFix(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.Fix);

    private void OnSyncNow(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.SyncNow);

    private void OnPause(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.TogglePause);

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.OpenFolder);

    private void OnDownloadNow(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.DownloadNow);

    private void OnUpdateServer(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.CheckNestUpdate);

    private void OnOpenAccount(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.OpenAccount);

    private void OnSeeList(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.SeeList);

    private void OnReviewAttention(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.Review);

    private void OnOverviewButton(object? sender, RoutedEventArgs e) => Act?.Invoke(OverviewAction.None);

    private void OnSeeAllActivity(object? sender, RoutedEventArgs e) => SeeAllActivity?.Invoke();

    // ------------------------------------------------------------------ the heading

    private static string HeroSignature(OverviewModel m) => string.Join('\u001f',
        m.State, m.Border, m.IsSyncingBar, m.Chip, m.ChipTone, m.Glyph, m.GlyphTone, m.Title, m.Badge, m.Detail,
        m.Progress, m.HeadButton, m.WaitBox, m.DeleteBox is null ? null : string.Join('|', m.DeleteBox.Files.Select(f => f.ToString())) + m.DeleteBox.CountText + m.DeleteBox.More,
        m.FolderBox, m.Note, string.Join('|', m.Checks), string.Join('|', m.Infos), string.Join('|', m.Tiles), string.Join('|', m.Buttons), m.ButtonNote);

    private Control BuildHero(OverviewModel m)
    {
        _pulse = null;
        return m.IsSyncingBar ? BuildSyncingHero(m) : BuildStateHero(m);
    }

    private static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid();
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
        return grid;
    }

    private static readonly GridLength Auto = GridLength.Auto;
    private static readonly GridLength Star = new(1, GridUnitType.Star);

    private Control BuildSyncingHero(OverviewModel m)
    {
        var stack = new StackPanel();
        var top = Columns(Auto, Star, Auto);

        var chip = new Border { Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        Paint(chip, Border.BackgroundProperty, Soft(m.ChipTone));
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(dot, Shape.FillProperty, m.ChipTone == OverviewTone.Warn ? "T.Warn" : "T.Accent");
        chipRow.Children.Add(dot);
        var chipText = Txt(m.Chip, 12, FontWeight.Bold, Ink(m.ChipTone));
        chipText.VerticalAlignment = VerticalAlignment.Center;
        chipRow.Children.Add(chipText);
        chip.Child = chipRow;
        top.Children.Add(chip);

        var title = Txt(m.Title, 18, FontWeight.Bold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        top.Children.Add(title);

        var percent = Txt(m.Progress?.Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty, 30, FontWeight.ExtraBold);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(percent, 2);
        top.Children.Add(percent);
        stack.Children.Add(top);

        var bar = Bar(m.Progress?.Percent ?? 0);
        bar.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(bar);

        var texts = Columns(Star, Auto);
        texts.Margin = new Thickness(0, 14, 0, 0);
        var left = Txt(m.Progress?.Left ?? string.Empty, 13, FontWeight.Normal, "T.Muted");
        var right = Txt(m.Progress?.Right ?? string.Empty, 13, FontWeight.Normal, m.Progress?.RightWarn == true ? "T.WarnText" : "T.Muted");
        right.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(right, 1);
        texts.Children.Add(left);
        texts.Children.Add(right);
        stack.Children.Add(texts);
        return stack;
    }

    private Control BuildStateHero(OverviewModel m)
    {
        var stack = new StackPanel();
        var head = Columns(Auto, Star, Auto);

        var circle = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        Paint(circle, Border.BackgroundProperty, Soft(m.GlyphTone));
        circle.Child = Glyph(m.Glyph, m.Glyph == OverviewIcon.Pause ? 24 : 26, Ink(m.GlyphTone), m.Glyph is OverviewIcon.Check or OverviewIcon.Pause ? 2.6 : 2);
        head.Children.Add(circle);
        if (m.Pulses)
        {
            _pulse = circle;
            circle.Classes.Add("pulse");
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Txt(m.Title, 22, FontWeight.ExtraBold);
        title.TextTrimming = TextTrimming.None;
        titleRow.Children.Add(title);
        if (m.Badge is { } badge)
        {
            var pill = Pill(badge, m.BadgeTone);
            pill.Margin = new Thickness(10, 0, 0, 0);
            titleRow.Children.Add(pill);
        }
        texts.Children.Add(titleRow);
        if (m.Detail.Length > 0)
            texts.Children.Add(Txt(m.Detail, 14, FontWeight.Normal, "T.Muted", wrap: true));
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

    private static Control Space(Control element, double top = 16)
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    private Control BuildWaitBox(OverviewWaitBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
        Paint(card, Border.BorderBrushProperty, "T.Border");
        var stack = new StackPanel();
        var row = Columns(Auto, Star, Auto);
        var icon = Square(OverviewIcon.Computer, 32, 16, 10, "T.AccentSoft", "T.AccentInk");
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(box.Title, 14, FontWeight.Bold));
        texts.Children.Add(Txt(box.Sub, 12, FontWeight.Normal, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var pct = Txt(box.Percent.ToString(CultureInfo.InvariantCulture) + "%", 24, FontWeight.ExtraBold);
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

    private Control BuildDeleteBox(OverviewDeleteBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
        Paint(card, Border.BackgroundProperty, "T.WarnPill");
        Paint(card, Border.BorderBrushProperty, "T.WarnBorder");
        var stack = new StackPanel();
        var head = Columns(Star, Auto);
        head.Children.Add(Txt(box.Heading, 12, FontWeight.Bold, "T.WarnText"));
        var count = Txt(box.CountText, 12, FontWeight.Normal, "T.Muted");
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        stack.Children.Add(head);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var file in box.Files)
        {
            var item = Columns(Auto, Star);
            item.Margin = new Thickness(0, 0, 20, 8);
            var icon = Square(file.Icon, 30, 15, 9, "T.Card", "T.Muted");
            icon.Margin = new Thickness(0, 0, 10, 0);
            item.Children.Add(icon);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Txt(file.Name, 13, FontWeight.SemiBold));
            texts.Children.Add(Txt(file.Sub, 12, FontWeight.Normal, "T.TextSoft"));
            Grid.SetColumn(texts, 1);
            item.Children.Add(texts);
            grid.Children.Add(item);
        }
        stack.Children.Add(grid);
        if (box.More is { } more)
            stack.Children.Add(Txt(more, 12, FontWeight.Normal, "T.Muted"));
        card.Child = stack;
        return card;
    }

    private Control BuildFolderBox(OverviewFolderBox box)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12) };
        var row = Columns(Auto, Star, Auto);
        var icon = Square(OverviewIcon.Folder, 36, 18, 11, "T.Chip", "T.TextSoft");
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(box.Path, 14, FontWeight.Bold));
        texts.Children.Add(Txt(box.Sub, 12, FontWeight.Normal, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var badge = Pill(box.Badge, OverviewTone.Warn);
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);
        card.Child = row;
        // A dashed outline: a rectangle behind the content draws it.
        var host = new Grid();
        var dashed = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 4, 3 }, IsHitTestVisible = false };
        Paint(dashed, Shape.StrokeProperty, "T.WarnBorder");
        host.Children.Add(dashed);
        host.Children.Add(card);
        return host;
    }

    private Control BuildNote(string text, OverviewTone tone)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
        Paint(card, Border.BackgroundProperty, tone == OverviewTone.Bad ? "T.BadPill" : "T.Chip");
        Paint(card, Border.BorderBrushProperty, tone == OverviewTone.Bad ? "T.BadBorder" : "T.Border");
        var row = Columns(Auto, Star);
        var icon = Glyph(OverviewIcon.Info, 18, Ink(tone), 2);
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        var label = Txt(text, 13, FontWeight.Normal, "T.Text", wrap: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        card.Child = row;
        return card;
    }

    private static Control BuildInfoRow(IReadOnlyList<OverviewInfo> items, double indent, bool check)
    {
        var panel = new WrapPanel { Margin = new Thickness(indent, 0, 0, 0) };
        foreach (var item in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 4) };
            if (check)
            {
                var ok = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0), Child = Glyph(OverviewIcon.Check, 12, "T.OkText", 3) };
                Paint(ok, Border.BackgroundProperty, "T.OkPill");
                row.Children.Add(ok);
            }
            else
            {
                var icon = Glyph(item.Icon, 16, "T.Muted", 1.9);
                icon.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(icon);
            }
            var text = Txt(item.Text, 13, FontWeight.Normal, "T.Muted");
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildTiles(IReadOnlyList<OverviewTile> tiles)
    {
        var grid = new UniformGrid { Columns = tiles.Count };
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(i == 0 ? 0 : 6, 0, i == tiles.Count - 1 ? 0 : 6, 0) };
            Paint(card, Border.BorderBrushProperty, "T.Border");
            var stack = new StackPanel();
            stack.Children.Add(Txt(tile.Label, 12, FontWeight.Normal, "T.TextSoft"));
            stack.Children.Add(Txt(tile.Value, 15, FontWeight.Bold));
            if (tile.Sub is { } sub)
                stack.Children.Add(Txt(sub, 12, FontWeight.Normal, "T.Muted"));
            card.Child = stack;
            grid.Children.Add(card);
        }
        return grid;
    }

    private Control BuildButtons(OverviewModel m, double indent)
    {
        var row = Columns(Auto, Star);
        row.Margin = new Thickness(indent, 0, 0, 0);
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
            var text = Txt(note, 12, FontWeight.Normal, "T.TextSoft");
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
        _needs.IsVisible = false;
        Paint(_needs, Border.BackgroundProperty, "T.WarnPill");
        Paint(_needs, Border.BorderBrushProperty, "T.WarnBorder");
        var row = Columns(Auto, Auto, Star, Auto);
        var icon = Glyph(OverviewIcon.Warning, 18, "T.WarnText", 2);
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        _needsTitle.FontWeight = FontWeight.Bold;
        _needsTitle.VerticalAlignment = VerticalAlignment.Center;
        _needsTitle.Margin = new Thickness(0, 0, 12, 0);
        Paint(_needsTitle, TextBlock.ForegroundProperty, "T.WarnText");
        Grid.SetColumn(_needsTitle, 1);
        row.Children.Add(_needsTitle);
        _needsDetail.VerticalAlignment = VerticalAlignment.Center;
        _needsDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        Paint(_needsDetail, TextBlock.ForegroundProperty, "T.Muted");
        Grid.SetColumn(_needsDetail, 2);
        row.Children.Add(_needsDetail);
        var review = new Button { Content = "Review", Height = 34, MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), FontWeight = FontWeight.Bold, Margin = new Thickness(12, 0, 0, 0), CornerRadius = new CornerRadius(10) };
        review.Classes.Add("subtle");
        Paint(review, Button.BorderBrushProperty, "T.WarnBorder");
        review.Click += OnReviewAttention;
        Grid.SetColumn(review, 3);
        row.Children.Add(review);
        _needs.Child = row;
    }

    // ------------------------------------------------------------------ the cards below

    private static string LowerSignature(OverviewModel m) => string.Join('\u001f',
        m.Lower, m.MovingCount, string.Join('|', m.Moving.Select(r => r + string.Join(',', r.Files.Select(f => f.ToString())))),
        string.Join('|', m.Changes), string.Join('|', m.Facts));

    private Control BuildLower(OverviewModel m) => m.Lower switch
    {
        OverviewLower.Moving => BuildMoving(m),
        OverviewLower.ChangesAndFacts => BuildChangesAndFacts(m),
        _ => BuildStrip(m),
    };

    private static Border Card(Control child, Thickness padding)
    {
        var card = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = padding, Child = child };
        Paint(card, Border.BackgroundProperty, "T.Card");
        Paint(card, Border.BorderBrushProperty, "T.Border");
        return card;
    }

    private Control BuildMoving(OverviewModel m)
    {
        var stack = new StackPanel();
        var head = Columns(Star, Auto);
        head.Children.Add(Txt("Moving now", 16, FontWeight.ExtraBold));
        var count = Txt(m.MovingCount, 12, FontWeight.Normal, "T.TextSoft");
        count.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        stack.Children.Add(head);

        if (m.Moving.Count == 0)
        {
            var empty = Txt(m.Flow == OverviewFlow.Checking ? "Pairnets is looking through your folder. Files show up here when they start moving." : "Files show up here as they start moving.", 13, FontWeight.Normal, "T.Muted", wrap: true);
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
    private bool IsOpen(string key, bool first) => _expanded.Contains(key) || (first && !_collapsed.Contains(key));

    private Control BuildMovingRow(OverviewMovingRow row, bool open)
    {
        var grid = Columns(Auto, Star, Auto, Auto);
        grid.Margin = new Thickness(0, 12, 0, 0);
        grid.Background = Brushes.Transparent;
        var icon = Square(row.Icon, 36, 18, 11, "T.Chip", row.IsFolder ? "T.Folder" : "T.Muted", row.IsFolder ? 1.4 : 1.8);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(row.Title, row.IsFolder ? 14 : 13, row.IsFolder ? FontWeight.Bold : FontWeight.SemiBold));
        if (row.Sub.Length > 0)
            texts.Children.Add(Txt(row.Sub, 12, FontWeight.Normal, "T.TextSoft"));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (row.IsFolder)
        {
            if (open)
            {
                var chevron = ChevronGlyph(true);
                Grid.SetColumn(chevron, 3);
                grid.Children.Add(chevron);
            }
            else
            {
                AddBar(grid, 2, row.Percent, row.PercentText);
                var chevron = ChevronGlyph(false);
                chevron.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(chevron, 4);
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.Children.Add(chevron);
            }
            grid.Cursor = new Cursor(StandardCursorType.Hand);
            var key = row.Key;
            grid.PointerReleased += (_, _) =>
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

    private static Control ChevronGlyph(bool up)
    {
        var path = new Path { Data = Geometry.Parse(up ? "m6 15 6-6 6 6" : "m6 9 6 6 6-6"), StrokeThickness = 2.2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        Paint(path, Shape.StrokeProperty, "T.Muted");
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = 16, Height = 16, Child = canvas, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    }

    private static void AddBar(Grid grid, int barColumn, int? percent, string text)
    {
        var bar = Bar(percent ?? 0, 6);
        bar.Width = 180;
        bar.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(bar, barColumn);
        grid.Children.Add(bar);
        var label = Txt(text, 12, FontWeight.Bold, "T.Muted");
        label.Width = 36;
        label.TextAlignment = TextAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(label, barColumn + 1);
        grid.Children.Add(label);
    }

    private static Control BuildMovingFile(OverviewMovingFile file)
    {
        var grid = Columns(Star, Auto, Auto);
        grid.Margin = new Thickness(48, 10, 0, 0);
        var name = Txt(file.Name, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        if (file.Percent is { } p)
        {
            AddBar(grid, 1, p, p.ToString(CultureInfo.InvariantCulture) + "%");
        }
        else
        {
            var wait = Txt(file.StateText, 12, FontWeight.Normal, "T.TextSoft");
            wait.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(wait, 2);
            grid.Children.Add(wait);
        }
        return grid;
    }

    private Control BuildChangesAndFacts(OverviewModel m)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1.5, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(16, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var left = new StackPanel();
        var head = Columns(Star, Auto);
        head.Children.Add(Txt("Latest changes", 16, FontWeight.ExtraBold));
        var see = new Button { Content = "See all in Activity", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -3) };
        see.Classes.Add("link");
        see.Click += OnSeeAllActivity;
        Grid.SetColumn(see, 1);
        head.Children.Add(see);
        left.Children.Add(head);
        if (m.Changes.Count == 0)
        {
            var empty = Txt("Nothing has happened yet. Changes appear here as they sync.", 13, FontWeight.Normal, "T.Muted", wrap: true);
            empty.Margin = new Thickness(0, 12, 0, 0);
            left.Children.Add(empty);
        }
        foreach (var change in m.Changes)
        {
            var row = Columns(Auto, Star, Auto);
            row.Margin = new Thickness(0, 12, 0, 0);
            var icon = Square(change.Icon, 34, 16, 11, "T.Chip", Ink(change.Tone), 2.2);
            icon.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(icon);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Txt(change.Name, 14, FontWeight.SemiBold));
            texts.Children.Add(Txt(change.Sub, 12, FontWeight.Normal, "T.TextSoft"));
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var time = Txt(change.Time, 12, FontWeight.Normal, "T.TextSoft");
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 2);
            row.Children.Add(time);
            left.Children.Add(row);
        }
        grid.Children.Add(Card(left, new Thickness(22, 18, 22, 18)));

        var right = new Grid();
        for (var i = 0; i < m.Facts.Count; i++)
        {
            right.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 66 });
            var fact = BuildFact(m.Facts[i], i > 0);
            Grid.SetRow(fact, i);
            right.Children.Add(fact);
        }
        var factCard = Card(right, new Thickness(0));
        Grid.SetColumn(factCard, 2);
        grid.Children.Add(factCard);
        return grid;
    }

    private Control BuildFact(OverviewFact fact, bool divider)
    {
        var row = Columns(Auto, Star, Auto);
        row.Margin = new Thickness(18, 0, 18, 0);
        var isFolder = fact.Icon is OverviewIcon.FolderFill;
        var icon = Square(fact.Icon, 34, 16, 11, "T.Chip", isFolder ? "T.Folder" : "T.Muted", isFolder ? 1.4 : 1.9);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(fact.Label, 12, FontWeight.Normal, "T.TextSoft"));
        texts.Children.Add(Txt(fact.Value, 14, FontWeight.Bold, fact.Tone == OverviewTone.Neutral ? "T.Text" : Ink(fact.Tone)));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (fact.LinkText is { } link)
        {
            var button = new Button { Content = link, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, -6, 0) };
            button.Classes.Add("link");
            var action = fact.LinkAction;
            button.Click += HandlerFor(action);
            if (action == OverviewAction.CheckNestUpdate)
                button.Name = "VersionButton";
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        if (!divider)
            return row;
        var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        Paint(wrap, Border.BorderBrushProperty, "T.Border");
        return wrap;
    }

    private Control BuildStrip(OverviewModel m)
    {
        var grid = new Grid();
        for (var i = 0; i < m.Facts.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var cell = new Border { BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Padding = new Thickness(0, 14, 0, 14) };
            Paint(cell, Border.BorderBrushProperty, "T.Border");
            cell.Child = BuildFact(m.Facts[i], false);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return Card(grid, new Thickness(0));
    }
}
