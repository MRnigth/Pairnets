using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Tether.Core.Client;

namespace Tether.Client.Ui;

/// <summary>
/// A burst of confetti from <see cref="Origin"/> (the first sync after setup), drawn from
/// <see cref="ConfettiBurst"/> like the Mac/Linux app. Nothing is drawn before or after, and
/// nothing plays when Windows is set to show fewer animations.
/// </summary>
public sealed class Confetti : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Confetti), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly string[] ColorKeys = ["S.Green", "S.Blue", "S.Yellow", "S.Orange"];

    public Confetti()
    {
        IsHitTestVisible = false;
        ClipToBounds = true; // stays inside the status card
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Where the pieces fly out from (the middle of the status badge).</summary>
    public Point Origin { get; set; }

    /// <summary>True while the confetti flies (for the screenshot tool and checks).</summary>
    public bool IsBursting { get; private set; }

    public void Burst()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var burst = new DoubleAnimation(0, 1, ConfettiBurst.Duration) { FillBehavior = FillBehavior.Stop };
        burst.Completed += (_, _) => IsBursting = false;
        IsBursting = true;
        BeginAnimation(ProgressProperty, burst);
    }

    public void Stop()
    {
        IsBursting = false;
        BeginAnimation(ProgressProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var piece in ConfettiBurst.At(Progress))
        {
            if (Visuals.Resource<Brush>(ColorKeys[piece.Color]) is not { } brush)
                continue;
            dc.PushTransform(new TranslateTransform(Origin.X + piece.X, Origin.Y + piece.Y));
            dc.PushTransform(new RotateTransform(piece.Angle));
            dc.PushOpacity(piece.Opacity);
            dc.DrawRoundedRectangle(brush, null, new Rect(-piece.Width / 2, -piece.Height / 2, piece.Width, piece.Height), 1, 1);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }
}
