namespace Pairnets.Core.Client;

/// <summary>What colour family the ring is drawn in: the app's blue, green, amber and red (or quiet grey).</summary>
public enum RingTone
{
    Accent,
    Ok,
    Warn,
    Bad,
    Quiet,
}

/// <summary>
/// The Developer beta look's two-layer progress ring on the Overview card, as plain values (no UI types): layer 1 is the
/// strong arc (files done this pass), layer 2 the lighter arc that carries on from it (files moving right now), and the
/// centre says the count. Built from <see cref="StatusSnapshot"/> only; both apps draw it the same way.
/// </summary>
/// <param name="Done">0–1: the strong layer, from 12 o'clock clockwise.</param>
/// <param name="Moving">0–1: how much further the lighter layer reaches past <paramref name="Done"/>.</param>
/// <param name="Big">The big centre text ("312"), or empty while <paramref name="Thinking"/>.</param>
/// <param name="Small">The small line under it ("of 1,204"), or empty.</param>
/// <param name="Thinking">No count exists yet (reading the server's list): three dots wave in the centre.</param>
/// <param name="Syncing">A thin arc circles the ring while a pass runs.</param>
public sealed record ProgressRingModel(double Done, double Moving, string Big, string Small, RingTone Tone, bool Thinking, bool Syncing)
{
    /// <summary>Where the lighter layer ends: never past a full turn.</summary>
    public double Reach => Math.Clamp(Done + Moving, 0, 1);

    /// <summary>The words a screen reader says for the ring.</summary>
    public string Description => Thinking
        ? "Reading the server's list"
        : string.IsNullOrEmpty(Small) ? Big : $"{Big} {Small}";

    public static ProgressRingModel Build(StatusSnapshot s, NestMapState state)
    {
        var tone = ToneOf(state);
        switch (state)
        {
            case NestMapState.Syncing:
                if (s.FilesTotal > 0 && !s.IsChecking)
                {
                    var done = Math.Min(s.FilesDone, s.FilesTotal);
                    return new(Clamp01(done / (double)s.FilesTotal), Clamp01(s.Active.Count / (double)s.FilesTotal),
                        Format.Count(done), "of " + Format.Count(s.FilesTotal), tone, false, true);
                }
                if (s.IsChecking && s.Scan is { FilesTotal: > 0 } scan)
                    return new(Clamp01(scan.FilesChecked / (double)scan.FilesTotal), 0,
                        Format.Count(scan.FilesChecked), "of " + Format.Count(scan.FilesTotal) + " checked", tone, false, true);
                return new(0, 0, string.Empty, string.Empty, tone, true, true);

            case NestMapState.UpToDate:
                return new(1, 0, "✓", string.Empty, tone, false, false);

            case NestMapState.Waiting:
            case NestMapState.Stopped:
                if (s.FilesTotal > 0)
                {
                    var done = Math.Min(s.FilesDone, s.FilesTotal);
                    return new(Clamp01(done / (double)s.FilesTotal), 0, Format.Count(done), "of " + Format.Count(s.FilesTotal), tone, false, false);
                }
                return new(0, 0, "!", string.Empty, tone, false, false);

            case NestMapState.Paused:
                return new(0, 0, "II", string.Empty, tone, false, false);

            default:
                // Offline, signed out, signing in, folder missing: an empty ring with a mark, no motion.
                return new(0, 0, "!", string.Empty, tone, false, false);
        }
    }

    public static RingTone ToneOf(NestMapState state) => state switch
    {
        NestMapState.Syncing => RingTone.Accent,
        NestMapState.UpToDate => RingTone.Ok,
        NestMapState.Waiting or NestMapState.Stopped or NestMapState.FolderMissing => RingTone.Warn,
        NestMapState.Offline or NestMapState.SignedOut or NestMapState.SignIn => RingTone.Bad,
        _ => RingTone.Quiet,
    };

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
