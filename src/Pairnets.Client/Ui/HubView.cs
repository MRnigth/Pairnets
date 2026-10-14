using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The nest and every computer (Hub.dc.html): this computer on the left, its line to the nest, the nest in the middle, then a
/// curved line from the nest to each other computer with its icon, name and status. It only draws a <see cref="NestMapModel"/>.
/// The lines of computers that move files carry a few light dashes that travel along them (not when Windows shows fewer
/// animations).
/// </summary>
public sealed class HubView : UserControl
{
    private const double Height168 = 168;
    private const int Slots = 5;

    private readonly Grid _root = new() { Height = Height168 };
    private readonly Border _thisIcon = new();
    private readonly LineIcon _thisGlyph = Icon(19, 1.8);
    private readonly Ellipse _thisDot = Dot(11);
    private readonly TextBlock _thisName = new();
    private readonly Grid _thisLine = new();
    private readonly HubStroke _thisLeft = new();
    private readonly HubStroke _thisRight = new();
    private readonly Border _thisPill = new();
    private readonly TextBlock _thisPillText = new();
    private readonly TextBlock _thisLabel = new();
    private readonly Border _thisCross = Cross();
    private readonly Border _nestBox = new();
    private readonly LineIcon _nestGlyph = Icon(24, 1.7);
    private readonly Ellipse _nestDot = Dot(12);
    private readonly TextBlock _nestSub = new();
    private readonly Canvas _right = new() { Width = 96 + 334, Height = Height168 };
    private readonly Slot[] _slots = new Slot[Slots];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double _phase;
    private DateTime _last;
    private bool _animate;

    /// <summary>Clicked on "+N more" (the page that lists every computer opens).</summary>
    public event Action? MoreClicked;

    public HubView()
    {
        Focusable = false;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });

        BuildThis();
        BuildLine();
        BuildNest();
        _right.HorizontalAlignment = HorizontalAlignment.Left;
        for (var i = 0; i < Slots; i++)
        {
            _slots[i] = new Slot(this);
            _slots[i].AddTo(_right);
        }
        Grid.SetColumn(_right, 3);
        _root.Children.Add(_right);
        Content = _root;

        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    /// <summary>Show the speed on the lines (Settings: "Show speed on the map").</summary>
    public bool ShowSpeed { get; set; } = true;

    /// <summary>True while dashes travel along this computer's own line (the checks).</summary>
    internal bool ThisFlowing => _thisLeft.Flowing;

    /// <summary>What the first other computer's line says ("DESKTOP: online"), for the checks.</summary>
    internal string OtherText => _slots[0].Text;

    /// <summary>The drawing of this computer's icon and of the first other computer's icon, for the checks.</summary>
    internal Geometry? ThisIconData => _thisGlyph.Data;

    internal Geometry? OtherIconData => _slots[0].IconData;

    // ------------------------------------------------------------------ building

    private static LineIcon Icon(double size, double thickness) => new() { Width = size, Height = size, StrokeThickness = thickness };

    private static Ellipse Dot(double size)
    {
        var dot = new Ellipse { Width = size, Height = size, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
        dot.SetResourceReference(Shape.StrokeProperty, "T.Card");
        return dot;
    }

    private static Border Cross()
    {
        var cross = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed };
        cross.SetResourceReference(Border.BackgroundProperty, "T.BadPill");
        cross.SetResourceReference(Border.BorderBrushProperty, "T.Bad");
        var x = new Path { Data = Geometry.Parse("M2 2l6 6M8 2l-6 6"), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 10, Height = 10, Stretch = Stretch.None };
        x.SetResourceReference(Shape.StrokeProperty, "T.Bad");
        cross.Child = x;
        return cross;
    }

    private void BuildThis()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _thisIcon.Width = _thisIcon.Height = 40;
        _thisIcon.CornerRadius = new CornerRadius(12);
        _thisIcon.SetResourceReference(Border.BackgroundProperty, "T.Chip");
        var inner = new Grid();
        _thisGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _thisGlyph.VerticalAlignment = VerticalAlignment.Center;
        _thisGlyph.SetResourceReference(LineIcon.StrokeProperty, "T.Text");
        inner.Children.Add(_thisGlyph);
        inner.Children.Add(_thisDot);
        _thisIcon.Child = inner;
        _thisIcon.Margin = new Thickness(0, 0, 10, 0);
        stack.Children.Add(_thisIcon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _thisName.FontWeight = FontWeights.Bold;
        _thisName.TextTrimming = TextTrimming.CharacterEllipsis;
        _thisName.MaxWidth = 84;
        _thisName.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        var sub = new TextBlock { Text = "This computer", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 84 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSoft");
        texts.Children.Add(_thisName);
        texts.Children.Add(sub);
        stack.Children.Add(texts);
        Grid.SetColumn(stack, 0);
        _root.Children.Add(stack);
    }

    private void BuildLine()
    {
        _thisLine.Margin = new Thickness(8, 0, 0, 0);
        _thisLine.VerticalAlignment = VerticalAlignment.Center;
        _thisLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _thisLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _thisLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _thisPill.Height = 22;
        _thisPill.CornerRadius = new CornerRadius(11);
        _thisPill.Padding = new Thickness(8, 0, 8, 0);
        _thisPill.Margin = new Thickness(6, 0, 6, 0);
        _thisPill.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
        _thisPillText.FontSize = 11;
        _thisPillText.FontWeight = FontWeights.ExtraBold;
        _thisPillText.VerticalAlignment = VerticalAlignment.Center;
        _thisPillText.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
        _thisPill.Child = _thisPillText;
        _thisLabel.FontSize = 12;
        _thisLabel.FontWeight = FontWeights.Bold;
        _thisLabel.VerticalAlignment = VerticalAlignment.Center;
        _thisLabel.Margin = new Thickness(6, 0, 6, 0);
        _thisCross.Margin = new Thickness(6, 0, 6, 0);
        var middle = new Grid { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(_thisPill);
        middle.Children.Add(_thisLabel);
        middle.Children.Add(_thisCross);
        Grid.SetColumn(_thisLeft, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(_thisRight, 2);
        _thisLine.Children.Add(_thisLeft);
        _thisLine.Children.Add(middle);
        _thisLine.Children.Add(_thisRight);
        Grid.SetColumn(_thisLine, 1);
        _root.Children.Add(_thisLine);
    }

    private void BuildNest()
    {
        var host = new Grid { Width = 52 };
        _nestBox.Width = _nestBox.Height = 52;
        _nestBox.CornerRadius = new CornerRadius(16);
        _nestBox.VerticalAlignment = VerticalAlignment.Center;
        var inner = new Grid();
        _nestGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _nestGlyph.VerticalAlignment = VerticalAlignment.Center;
        _nestGlyph.Data = Visuals.Resource<Geometry>("I.Nest");
        inner.Children.Add(_nestGlyph);
        inner.Children.Add(_nestDot);
        _nestBox.Child = inner;
        host.Children.Add(_nestBox);

        var label = new StackPanel { Width = 150 };
        var title = new TextBlock { Text = "Your nest", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        _nestSub.FontSize = 12;
        _nestSub.HorizontalAlignment = HorizontalAlignment.Center;
        label.Children.Add(title);
        label.Children.Add(_nestSub);
        var labelCanvas = new Canvas { Width = 52, Height = Height168 };
        Canvas.SetLeft(label, -49);
        Canvas.SetTop(label, 116);
        labelCanvas.Children.Add(label);
        host.Children.Add(labelCanvas);
        Grid.SetColumn(host, 2);
        _root.Children.Add(host);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>The brush, thickness and dashing of each kind of line.</summary>
    private static (string Brush, double Thickness, bool Dashed) LineStyle(NestLineKind kind) => kind switch
    {
        NestLineKind.Active => ("T.Accent", 2.5, false),
        NestLineKind.Ok => ("T.OkLine", 2.0, false),
        NestLineKind.Idle => ("T.Line", 2.0, false),
        NestLineKind.Next => ("T.Line", 2.0, true),
        NestLineKind.Warn => ("T.Warn", 2.0, true),
        _ => ("T.Bad", 2.0, true),
    };

    private static string InkKey(NestLineKind kind) => kind switch
    {
        NestLineKind.Active => "T.AccentInk",
        NestLineKind.Ok => "T.OkText",
        NestLineKind.Warn => "T.WarnText",
        NestLineKind.Bad or NestLineKind.Unreachable => "T.BadText",
        _ => "T.TextSoft",
    };

    private static string DotKey(NestDot dot) => dot switch
    {
        NestDot.Warn => "T.Warn",
        NestDot.Bad => "T.Bad",
        NestDot.Off => "T.Off",
        _ => "T.Ok",
    };

    public void Show(NestMapModel m, string? thisName)
    {
        // This computer
        _thisName.Text = string.IsNullOrEmpty(thisName) ? "This computer" : thisName;
        _thisGlyph.Data = DeviceIconArt.For(m.This.Icon);
        _thisDot.SetResourceReference(Shape.FillProperty, DotKey(m.This.Dot));
        var line = m.This;
        var pill = line.HasPill && ShowSpeed;
        _thisPill.Visibility = pill ? Visibility.Visible : Visibility.Collapsed;
        _thisPillText.Text = line.Label;
        _thisLabel.Visibility = !line.HasPill && !line.HasCross ? Visibility.Visible : Visibility.Collapsed;
        _thisLabel.Text = line.Label;
        _thisLabel.SetResourceReference(TextBlock.ForegroundProperty, InkKey(line.Kind));
        _thisCross.Visibility = line.HasCross ? Visibility.Visible : Visibility.Collapsed;
        var towardsNest = !line.Label.StartsWith('↓');
        _thisLeft.Apply(line.Kind, towardsNest ? 1 : -1);
        _thisRight.Apply(line.Kind, towardsNest ? 1 : -1);
        if (line.Kind == NestLineKind.Active && !ShowSpeed)
            _thisLabel.Visibility = Visibility.Collapsed;

        // The nest
        var (bg, ink) = m.NestDot switch
        {
            NestDot.Bad => ("T.BadPill", "T.BadText"),
            NestDot.Warn => ("T.WarnPill", "T.WarnText"),
            _ => ("T.AccentSoft", "T.AccentInk"),
        };
        _nestBox.SetResourceReference(Border.BackgroundProperty, bg);
        _nestGlyph.SetResourceReference(LineIcon.StrokeProperty, ink);
        _nestDot.SetResourceReference(Shape.FillProperty, DotKey(m.NestDot));
        _nestSub.Text = m.NestSub;
        _nestSub.SetResourceReference(TextBlock.ForegroundProperty, m.NestDot switch { NestDot.Bad => "T.BadText", NestDot.Warn => "T.WarnText", _ => "T.TextSoft" });

        // The others: up to five slots, the last one "+N more" when there are more than five.
        var shown = m.MoreCount > 0 ? m.Others.Take(Slots - 1).ToList() : m.Others.ToList();
        var more = m.MoreCount > 0 ? m.MoreCount + (m.Others.Count - shown.Count) : 0;
        var count = shown.Count + (more > 0 ? 1 : 0);
        for (var i = 0; i < Slots; i++)
        {
            var y = Height168 / 2 + (i - (count - 1) / 2.0) * 34;
            if (i < shown.Count)
                _slots[i].Show(shown[i], y, ShowSpeed);
            else if (i == shown.Count && more > 0)
                _slots[i].ShowMore(more, y);
            else
                _slots[i].Hide();
        }
        UpdateTimer();
    }

    // ------------------------------------------------------------------ motion

    private bool AnyFlowing => _thisLeft.Flowing || _slots.Any(s => s.Flowing);

    private void UpdateTimer()
    {
        _animate = IsVisible && IsLoaded && SystemParameters.ClientAreaAnimation && AnyFlowing;
        if (_animate && !_timer.IsEnabled)
        {
            _last = DateTime.UtcNow;
            _timer.Start();
        }
        else if (!_animate && _timer.IsEnabled)
        {
            _timer.Stop();
            _thisLeft.Move(0);
            _thisRight.Move(0);
            foreach (var slot in _slots)
                slot.Move(0);
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        _phase = (_phase + (now - _last).TotalSeconds * 46) % 32;
        _last = now;
        _thisLeft.Move(_phase);
        _thisRight.Move(_phase);
        foreach (var slot in _slots)
            slot.Move(_phase);
    }

    // ------------------------------------------------------------------ parts

    /// <summary>One straight stretch of a line: a solid or dashed line, with light dashes travelling along it while files move.</summary>
    private sealed class HubStroke : FrameworkElement
    {
        private static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
            nameof(BaseBrush), typeof(Brush), typeof(HubStroke), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly DependencyProperty FlowBrushProperty = DependencyProperty.Register(
            nameof(FlowBrush), typeof(Brush), typeof(HubStroke), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        private double _thickness = 2;
        private bool _dashed;
        private int _direction = 1;
        private double _phase;

        public HubStroke()
        {
            Height = 4;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            IsHitTestVisible = false;
            SetResourceReference(FlowBrushProperty, "T.AccentInk");
        }

        private Brush BaseBrush => (Brush)GetValue(BaseBrushProperty);

        private Brush FlowBrush => (Brush)GetValue(FlowBrushProperty);

        public bool Flowing { get; private set; }

        public void Apply(NestLineKind kind, int direction)
        {
            _direction = direction;
            var (brush, thickness, dashed) = LineStyle(kind);
            SetResourceReference(BaseBrushProperty, brush);
            _thickness = thickness;
            _dashed = dashed;
            Flowing = kind == NestLineKind.Active;
            InvalidateVisual();
        }

        public void Move(double phase)
        {
            _phase = phase;
            if (Flowing)
                InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var y = ActualHeight / 2;
            if (w <= 1)
                return;
            var pen = new Pen(BaseBrush, _thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_dashed)
                pen.DashStyle = new DashStyle([2.5, 2], 0);
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            if (!Flowing)
                return;
            var flow = new Pen(FlowBrush, _thickness) { DashStyle = new DashStyle([3.2, 12], -_direction * _phase / _thickness), DashCap = PenLineCap.Round };
            dc.PushOpacity(0.85);
            dc.DrawLine(flow, new Point(0, y), new Point(w, y));
            dc.Pop();
        }
    }

    /// <summary>The curve from the nest to one computer, with the same travelling dashes.</summary>
    private sealed class HubCurve : Grid
    {
        private readonly Path _base = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Path _flow = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        private int _direction = 1;

        public HubCurve()
        {
            Width = 96;
            Height = Height168;
            IsHitTestVisible = false;
            Children.Add(_base);
            Children.Add(_flow);
            _flow.SetResourceReference(Shape.StrokeProperty, "T.AccentInk");
            _flow.Opacity = 0.85;
        }

        public bool Flowing { get; private set; }

        public void Apply(NestLineKind kind, double endY, int direction)
        {
            _direction = direction;
            var data = Math.Abs(endY - 84) < 0.5
                ? "M0 84 L96 84"
                : FormattableString.Invariant($"M0 84 C48 84 48 {endY:0.##} 96 {endY:0.##}");
            _base.Data = _flow.Data = Geometry.Parse(data);
            var (brush, thickness, dashed) = LineStyle(kind);
            _base.SetResourceReference(Shape.StrokeProperty, brush);
            _base.StrokeThickness = thickness;
            _base.StrokeDashArray = dashed ? new DoubleCollection([2.5, 2]) : null;
            Flowing = kind == NestLineKind.Active;
            _flow.Visibility = Flowing ? Visibility.Visible : Visibility.Collapsed;
            _flow.StrokeThickness = thickness;
            _flow.StrokeDashArray = new DoubleCollection([3.2, 12]);
            _flow.StrokeDashCap = PenLineCap.Round;
        }

        public void Move(double phase) => _flow.StrokeDashOffset = -_direction * phase / (_flow.StrokeThickness <= 0 ? 1 : _flow.StrokeThickness);
    }

    /// <summary>One other computer: its curve, the pill on its line, its icon, name and status.</summary>
    private sealed class Slot
    {
        private readonly HubView _hub;
        private readonly HubCurve _curve = new();
        private readonly Grid _row = new() { Width = 334, Height = 28 };
        private readonly HubStroke _line1 = new();
        private readonly HubStroke _line2 = new();
        private readonly Border _pill = new();
        private readonly TextBlock _pillText = new();
        private readonly Border _cross = Cross();
        private readonly Border _icon = new();
        private readonly LineIcon _glyph = Icon(14, 2);
        private readonly Ellipse _dot = Dot(9);
        private readonly TextBlock _name = new();
        private readonly TextBlock _status = new();
        private readonly Button _moreButton = new();
        private ColumnDefinition _segEnd = new();

        public Slot(HubView hub)
        {
            _hub = hub;
            _row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            _row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            _row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The 104 px beside the curve: [pill] line [x] line.
            var seg = new Grid { Width = 104 };
            seg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            seg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _segEnd = new ColumnDefinition { Width = new GridLength(0) };
            seg.ColumnDefinitions.Add(_segEnd);
            _pill.Height = 22;
            _pill.CornerRadius = new CornerRadius(11);
            _pill.BorderThickness = new Thickness(1.5);
            _pill.Padding = new Thickness(8, 0, 8, 0);
            _pill.Margin = new Thickness(0, 0, 4, 0);
            _pill.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
            _pill.SetResourceReference(Border.BorderBrushProperty, "T.Accent");
            _pillText.FontSize = 11;
            _pillText.FontWeight = FontWeights.ExtraBold;
            _pillText.VerticalAlignment = VerticalAlignment.Center;
            _pillText.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
            _pill.Child = _pillText;
            _cross.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(_pill, 0);
            Grid.SetColumn(_line1, 1);
            Grid.SetColumn(_cross, 2);
            Grid.SetColumn(_line2, 3);
            seg.Children.Add(_pill);
            seg.Children.Add(_line1);
            seg.Children.Add(_cross);
            seg.Children.Add(_line2);
            _row.Children.Add(seg);

            _icon.Width = _icon.Height = 28;
            _icon.CornerRadius = new CornerRadius(9);
            _icon.HorizontalAlignment = HorizontalAlignment.Left;
            _icon.Margin = new Thickness(8, 0, 0, 0);
            var inner = new Grid();
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            inner.Children.Add(_glyph);
            inner.Children.Add(_dot);
            _icon.Child = inner;
            Grid.SetColumn(_icon, 1);
            _row.Children.Add(_icon);

            _name.FontSize = 13;
            _name.FontWeight = FontWeights.Bold;
            _name.TextWrapping = TextWrapping.NoWrap;
            _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _name.VerticalAlignment = VerticalAlignment.Center;
            _name.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(_name, 2);
            _row.Children.Add(_name);
            _status.FontSize = 12;
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_status, 3);
            _row.Children.Add(_status);

            _moreButton.Style = (Style)Application.Current.FindResource("LinkButton");
            _moreButton.Visibility = Visibility.Collapsed;
            _moreButton.HorizontalAlignment = HorizontalAlignment.Left;
            _moreButton.VerticalAlignment = VerticalAlignment.Center;
            _moreButton.Margin = new Thickness(0, 0, 0, 0);
            _moreButton.Click += (_, _) => _hub.MoreClicked?.Invoke();
            Grid.SetColumn(_moreButton, 2);
            Grid.SetColumnSpan(_moreButton, 2);
            _row.Children.Add(_moreButton);
        }

        public bool Flowing => _curve.Visibility == Visibility.Visible && _curve.Flowing;

        public Geometry? IconData => _glyph.Data;

        public string Text => _name.Visibility == Visibility.Visible ? $"{_name.Text}: {_status.Text}" : string.Empty;

        public void AddTo(Canvas canvas)
        {
            canvas.Children.Add(_curve);
            Canvas.SetLeft(_curve, 0);
            Canvas.SetTop(_curve, 0);
            canvas.Children.Add(_row);
            Canvas.SetLeft(_row, 96);
        }

        public void Hide()
        {
            _curve.Visibility = _row.Visibility = Visibility.Collapsed;
        }

        public void Move(double phase)
        {
            _curve.Move(phase);
            _line1.Move(phase);
            _line2.Move(phase);
        }

        public void Show(NestMapOther o, double y, bool showSpeed)
        {
            _curve.Visibility = _row.Visibility = Visibility.Visible;
            Canvas.SetTop(_row, y - 14);
            // The files go towards the nest when this computer sends; otherwise away from it.
            var direction = o.GoesUp ? -1 : 1;
            _curve.Apply(o.Kind, y, direction);
            _line1.Apply(o.Kind, direction);
            _line2.Apply(o.Kind, direction);
            var pill = o.HasPill && showSpeed;
            _pill.Visibility = pill ? Visibility.Visible : Visibility.Collapsed;
            _pillText.Text = o.Speed ?? string.Empty;
            _pill.SetResourceReference(Border.BorderBrushProperty, "T.Accent");
            _cross.Visibility = o.HasCross ? Visibility.Visible : Visibility.Collapsed;
            _line2.Visibility = o.HasCross ? Visibility.Visible : Visibility.Collapsed;
            _segEnd.Width = o.HasCross ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (o.HasCross)
                _line2.Apply(o.Kind, direction);

            _moreButton.Visibility = Visibility.Collapsed;
            _name.Visibility = _status.Visibility = Visibility.Visible;
            _name.Text = o.Name;
            _name.SetResourceReference(TextBlock.ForegroundProperty, o.Online ? "T.Text" : "T.Muted");
            _status.Text = o.Status;
            _status.SetResourceReference(TextBlock.ForegroundProperty, InkKey(o.Kind));
            _icon.SetResourceReference(Border.BackgroundProperty, o.Online ? "T.Chip" : "T.ChipDim");
            _glyph.SetResourceReference(LineIcon.StrokeProperty, o.Online ? "T.Text" : "T.TextSoft");
            _glyph.Data = DeviceIconArt.For(o.Icon);
            _icon.Visibility = Visibility.Visible;
            _dot.SetResourceReference(Shape.FillProperty, DotKey(o.Dot));
            ToolTipService.SetToolTip(_row, $"{o.Name}: {o.Status}");
        }

        public void ShowMore(int more, double y)
        {
            _curve.Visibility = _row.Visibility = Visibility.Visible;
            Canvas.SetTop(_row, y - 14);
            _curve.Apply(NestLineKind.Next, y, 1);
            _line1.Apply(NestLineKind.Next, 1);
            _pill.Visibility = _cross.Visibility = _line2.Visibility = Visibility.Collapsed;
            _segEnd.Width = new GridLength(0);
            _icon.Visibility = Visibility.Collapsed;
            _name.Visibility = _status.Visibility = Visibility.Collapsed;
            _moreButton.Visibility = Visibility.Visible;
            _moreButton.Content = "+" + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";
            ToolTipService.SetToolTip(_row, null);
        }
    }
}
