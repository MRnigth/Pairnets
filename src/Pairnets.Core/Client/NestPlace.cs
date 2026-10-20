using Pairnets.Core.Api;

namespace Pairnets.Core.Client;

/// <summary>The steps of the "Where your nest keeps files" window, the same in both apps.</summary>
public enum NestPlaceStep
{
    /// <summary>Waiting for the nest's list of drives.</summary>
    Loading,

    /// <summary>The drives, to pick one.</summary>
    Choose,

    /// <summary>"Move your nest's files to …?"</summary>
    Confirm,

    /// <summary>Copying, then switching over.</summary>
    Moving,

    Done,
    Failed,
}

/// <summary>One drive in the window's list: its name, the folder a move would use, its space, and why it can't be used.</summary>
public sealed record NestPlaceRow(string Label, string Folder, string Space, bool Usable, string? Reason, bool Current, bool Main);

/// <summary>
/// What the "Where your nest keeps files" window shows (both apps draw it from this): the drives from
/// <c>GET /api/storage</c>, the choice, and how a move goes. The window asks the nest about once a second and passes every
/// answer to <see cref="Seen"/> (or a failed ask to <see cref="Unreachable"/>); the nest is offline for a moment while it
/// switches over, so a move carries on through that.
/// </summary>
public sealed class NestPlaceFlow(TimeProvider? clock = null)
{
    /// <summary>After this long without a list of drives the window says the nest did not list them.</summary>
    public static readonly TimeSpan SlowList = TimeSpan.FromSeconds(20);

    /// <summary>A nest that does not answer this long during a move is reported, not waited for forever.</summary>
    public static readonly TimeSpan OfflineLimit = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DateTimeOffset _opened = (clock ?? TimeProvider.System).GetUtcNow();
    private StorageMoveStatus? _baseline;
    private bool _watching;
    private DateTimeOffset? _offlineSince;

    public NestPlaceStep Step { get; private set; } = NestPlaceStep.Loading;

    /// <summary>The nest's last answer.</summary>
    public NestStorage? Storage { get; private set; }

    /// <summary>The folder picked on the list (the confirm step and the move use it).</summary>
    public string? Chosen { get; private set; }

    /// <summary>0 to 100 while moving.</summary>
    public int Percent { get; private set; }

    /// <summary>True while the nest does not answer (during a move: it is switching over).</summary>
    public bool Offline { get; private set; }

    /// <summary>The plain reason a move did not happen, or the nest's words when it worked.</summary>
    public string? ResultMessage { get; private set; }

    /// <summary>Where the files were before a move that worked (kept until the owner deletes them).</summary>
    public string? OldCopy { get; private set; }

    /// <summary>The nest's answer: the drives, and how a move goes.</summary>
    public void Seen(NestStorage storage)
    {
        Storage = storage;
        Offline = false;
        _offlineSince = null;
        var status = storage.Status;
        if (status is { IsBusy: true })
        {
            // A move runs (this window asked for it, or it was already running when the window opened).
            _watching = true;
            Step = NestPlaceStep.Moving;
            Percent = Math.Clamp(status.Percent, 0, 100);
            return;
        }
        if (_watching)
        {
            if (status is { State: StorageMoveStatus.Succeeded or StorageMoveStatus.Failed } && status != _baseline)
            {
                _watching = false;
                Step = status.State == StorageMoveStatus.Succeeded ? NestPlaceStep.Done : NestPlaceStep.Failed;
                Percent = Step == NestPlaceStep.Done ? 100 : Percent;
                ResultMessage = status.Message;
                OldCopy = string.IsNullOrWhiteSpace(status.OldCopy) ? null : status.OldCopy;
            }
            return; // the helper has not reported this move yet
        }
        if (Step == NestPlaceStep.Loading && (storage.Drives.Count > 0 || !storage.CanMove || storage.ByHand))
            Step = NestPlaceStep.Choose;
    }

    /// <summary>The nest did not answer (it pauses while it switches over to the new drive).</summary>
    public void Unreachable()
    {
        Offline = true;
        var now = _clock.GetUtcNow();
        _offlineSince ??= now;
        if (_watching && now - _offlineSince > OfflineLimit)
        {
            _watching = false;
            Step = NestPlaceStep.Failed;
            ResultMessage = $"Your nest has not answered for {OfflineLimit.TotalMinutes:0} minutes. It may still be finishing the move; open this again later to see how it went.";
        }
    }

    /// <summary>A drive on the list was picked: false (and nothing changes) when it can't be used or holds the files already.</summary>
    public bool Choose(string folder)
    {
        if (Storage is not { } storage || Step is not (NestPlaceStep.Choose or NestPlaceStep.Confirm))
            return false;
        var row = Rows().FirstOrDefault(r => r.Folder == folder);
        if (row is null || !row.Usable || row.Current)
            return false;
        Chosen = folder;
        Step = NestPlaceStep.Confirm;
        return true;
    }

    /// <summary>Back from the confirm step to the list.</summary>
    public void Back()
    {
        if (Step == NestPlaceStep.Confirm)
            Step = NestPlaceStep.Choose;
    }

    /// <summary>"Try again" after a move that did not happen: the list again.</summary>
    public void Retry()
    {
        if (Step is not (NestPlaceStep.Failed or NestPlaceStep.Done))
            return;
        _watching = false;
        ResultMessage = null;
        OldCopy = null;
        Chosen = null;
        Step = Storage is { } s && (s.Drives.Count > 0 || !s.CanMove || s.ByHand) ? NestPlaceStep.Choose : NestPlaceStep.Loading;
    }

    /// <summary>"Move" was pressed: the move is asked for; <see cref="Answered"/> follows with the nest's answer.</summary>
    public void Asking()
    {
        _baseline = Storage?.Status;
        _watching = true;
        Step = NestPlaceStep.Moving;
        Percent = 0;
    }

    /// <summary>The nest's answer to the move request.</summary>
    public void Answered(PairnetsApiClient.StorageMoveAnswer answer)
    {
        switch (answer.Outcome)
        {
            case PairnetsApiClient.StorageMoveOutcome.Requested:
                if (answer.Storage is { } storage)
                    Seen(storage);
                break;
            case PairnetsApiClient.StorageMoveOutcome.AlreadyThere:
                _watching = false;
                Step = NestPlaceStep.Done;
                ResultMessage = "Your nest already keeps its files there.";
                OldCopy = null;
                if (answer.Storage is { } same)
                    Storage = same;
                break;
            default:
                _watching = false;
                Step = NestPlaceStep.Failed;
                ResultMessage = answer.Message ?? "Your nest said no to the move.";
                break;
        }
    }

    /// <summary>Asking for the move went wrong before the nest answered.</summary>
    public void AskFailed(string reason)
    {
        _watching = false;
        Step = NestPlaceStep.Failed;
        ResultMessage = reason;
    }

    /// <summary>The drives, the main disk first, with the one holding the files now marked.</summary>
    public IReadOnlyList<NestPlaceRow> Rows()
    {
        if (Storage is not { } storage)
            return [];
        return storage.Drives
            .Select(d => new NestPlaceRow(
                string.IsNullOrWhiteSpace(d.Label) ? (d.Main ? "Main disk" : d.Mount) : d.Label,
                d.Folder,
                $"{Format.Bytes(d.Free)} free of {Format.Bytes(d.Total)}",
                d.Usable,
                d.Usable ? null : Sentence(d.Problem!),
                SamePlace(d.Folder, storage.Current),
                d.Main))
            .OrderByDescending(r => r.Main)
            .ToList();
    }

    public string Heading => Step switch
    {
        NestPlaceStep.Confirm => "Move your nest's files?",
        NestPlaceStep.Moving => "Moving your nest's files…",
        NestPlaceStep.Done => "Your nest's files are moved",
        NestPlaceStep.Failed => "Not moved",
        _ => "Where your nest keeps files",
    };

    public string Explanation => Step switch
    {
        NestPlaceStep.Loading when _clock.GetUtcNow() - _opened > SlowList =>
            "Your nest has not listed its drives yet. Check that it is on; this window keeps asking.",
        NestPlaceStep.Loading when Offline => "Pairnets can't reach your nest right now. This window keeps asking.",
        NestPlaceStep.Loading => "Looking at your nest's drives…",
        NestPlaceStep.Choose when Storage is { CanMove: false } => "This nest can't move its files from the app. Run the install command on the nest once (Linux only).",
        NestPlaceStep.Choose when Storage is { ByHand: true } =>
            "Your nest's files are on a drive that was set up by hand (in /etc/fstab). Change it there.",
        NestPlaceStep.Choose => "Pick the drive for your nest's files. Your files stay as they are while they copy.",
        NestPlaceStep.Confirm => ConfirmText(Chosen ?? string.Empty),
        NestPlaceStep.Moving => "Your nest keeps running while they copy, then pauses for a moment to switch over.",
        NestPlaceStep.Done => DoneText(),
        _ => ResultMessage ?? "The move did not happen. Your nest keeps its files where they were.",
    };

    /// <summary>"Copying your files… 40%", "Switching over…": the line under the bar while moving.</summary>
    public string ProgressText
    {
        get
        {
            var message = Storage?.Status?.Message ?? string.Empty;
            if (Offline && _watching)
                return "Switching over… your nest is restarting";
            if (Storage?.Status is { State: StorageMoveStatus.Requested } || (_watching && Storage?.Status is not { IsBusy: true }))
                return "Starting the move…";
            if (Percent >= 96 || message.Contains("Switching", StringComparison.OrdinalIgnoreCase))
                return "Switching over…";
            if (message.Contains("last changes", StringComparison.OrdinalIgnoreCase))
                return $"Copying the last changes… {Percent}%";
            if (message.Contains("Checking", StringComparison.OrdinalIgnoreCase))
                return "Checking the drive…";
            return $"Copying your files… {Percent}%";
        }
    }

    /// <summary>The Settings row's line: "/mnt/data/pairnets · 3.6 TB free".</summary>
    public static string PlaceLine(NestStorage storage) =>
        storage.Free is { } free ? $"{storage.Current} · {Format.Bytes(free)} free" : storage.Current;

    public static string ConfirmText(string folder) =>
        $"Move your nest's files to {folder}? Your nest keeps running while they copy, then pauses for a moment to switch over. The old copy is kept until you delete it.";

    /// <summary>The command that deletes the old copy, for the done step.</summary>
    public string? DeleteCommand => OldCopy is { } old ? $"sudo rm -rf '{old.Replace("'", "'\\''", StringComparison.Ordinal)}'" : null;

    private string DoneText()
    {
        if (OldCopy is null)
            return ResultMessage ?? "Your nest already keeps its files there.";
        var where = Storage?.Current ?? Chosen;
        return (where is null ? "Your nest now keeps its files in the new place." : $"Your nest now keeps its files in {where}.")
            + " The old copy stays until you delete it, so check that everything works first.";
    }

    private static string Sentence(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return t;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.EndsWith('.') ? t : t + ".";
    }

    private static bool SamePlace(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
}
