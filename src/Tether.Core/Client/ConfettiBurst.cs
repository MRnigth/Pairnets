namespace Tether.Core.Client;

/// <summary>One piece of confetti at a moment of the burst: offset from the origin in pixels, turn in degrees.</summary>
public readonly record struct ConfettiPiece(double X, double Y, double Angle, double Opacity, double Width, double Height, int Color);

/// <summary>
/// The confetti that marks the first sync after setup (both apps draw it the same way): pieces
/// fly out of the status badge, slow down, drift down and fade. The seed is fixed, so every burst
/// looks the same.
/// </summary>
public static class ConfettiBurst
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.5);

    /// <summary>Pieces use colours 0 to 3: green, blue, yellow, orange.</summary>
    public const int ColorCount = 4;

    private sealed record Seed(double Dx, double Dy, double Fall, double Spin, double Sway, double Phase, double Width, double Height, int Color);

    private static readonly Seed[] Seeds = MakeSeeds(36);

    private static Seed[] MakeSeeds(int count)
    {
        var random = new Random(20261006);
        var seeds = new Seed[count];
        for (var i = 0; i < count; i++)
        {
            // A spray to the right, like a party popper: the badge sits top left in a wide, low card.
            var angle = (-50 + random.NextDouble() * 95) * Math.PI / 180;
            var distance = 90 + random.NextDouble() * 470;
            var square = random.Next(3) == 0;
            seeds[i] = new Seed(
                Math.Cos(angle) * distance,
                Math.Sin(angle) * distance * 0.35,
                Fall: 40 + random.NextDouble() * 80,
                Spin: (random.NextDouble() - 0.5) * 1080,
                Sway: 3 + random.NextDouble() * 6,
                Phase: random.NextDouble() * Math.PI * 2,
                Width: square ? 5.5 : 8,
                Height: square ? 5.5 : 3.5,
                Color: i % ColorCount);
        }
        return seeds;
    }

    /// <summary>Every piece at <paramref name="progress"/> (0 to 1 over <see cref="Duration"/>); none before or after.</summary>
    public static IEnumerable<ConfettiPiece> At(double progress)
    {
        if (progress <= 0 || progress >= 1)
            yield break;
        var burst = 1 - Math.Pow(1 - progress, 3); // fast out, then slowing down
        var opacity = Math.Min(progress / 0.06, progress < 0.7 ? 1 : (1 - progress) / 0.3);
        foreach (var s in Seeds)
        {
            var x = s.Dx * burst + Math.Sin(progress * Math.PI * 3 + s.Phase) * s.Sway * progress;
            var y = s.Dy * burst + s.Fall * progress * progress;
            yield return new ConfettiPiece(x, y, s.Spin * progress, opacity, s.Width, s.Height, s.Color);
        }
    }
}
