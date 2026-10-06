using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

/// <summary>
/// A burst of confetti from <see cref="Origin"/> (the first sync after setup), drawn from
/// <see cref="ConfettiBurst"/>. The "burst" style class animates <see cref="Progress"/> (0 → 1);
/// nothing is drawn before or after.
/// </summary>
public sealed class Confetti : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<Confetti, double>(nameof(Progress));
    public static readonly StyledProperty<Point> OriginProperty = AvaloniaProperty.Register<Confetti, Point>(nameof(Origin));

    private static readonly string[] ColorKeys = ["S.Green", "S.Blue", "S.Yellow", "S.Orange"];

    static Confetti() => AffectsRender<Confetti>(ProgressProperty, OriginProperty);

    public Confetti() => IsHitTestVisible = false;

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Where the pieces fly out from (the middle of the status badge).</summary>
    public Point Origin
    {
        get => GetValue(OriginProperty);
        set => SetValue(OriginProperty, value);
    }

    public void Burst() => Motion.Once(this, "burst");

    public override void Render(DrawingContext context)
    {
        foreach (var piece in ConfettiBurst.At(Progress))
        {
            if (Visuals.Resource<IBrush>(ColorKeys[piece.Color]) is not { } brush)
                continue;
            var turn = Matrix.CreateRotation(piece.Angle * Math.PI / 180) * Matrix.CreateTranslation(Origin.X + piece.X, Origin.Y + piece.Y);
            using (context.PushTransform(turn))
            using (context.PushOpacity(piece.Opacity))
                context.DrawRectangle(brush, null, new Rect(-piece.Width / 2, -piece.Height / 2, piece.Width, piece.Height), 1, 1);
        }
    }
}
