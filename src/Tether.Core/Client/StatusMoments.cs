using Tether.Core.Sync;

namespace Tether.Core.Client;

/// <summary>A moment the windows mark with a short, one-time animation.</summary>
public enum StatusMoment
{
    None,

    /// <summary>A sync that moved files has finished: everything is up to date again.</summary>
    SyncFinished,

    /// <summary>The first sync after setting Tether up has finished.</summary>
    FirstSync,
}

/// <summary>
/// Picks the moments worth a small celebration from one status snapshot to the next (both apps,
/// main window and tray panel). The UI refreshes about 4 times a second, so each moment is
/// reported once. A pass that moved nothing (most of them) is not a moment.
/// </summary>
public sealed class StatusMoments
{
    private bool _moving;
    private bool _firstSyncExpected;

    /// <summary>Called after first-time setup: the next time everything is up to date is the first sync.</summary>
    public void ExpectFirstSync() => _firstSyncExpected = true;

    /// <summary>Forgets what was seen (the window was hidden), so nothing plays late when it comes back.</summary>
    public void Reset()
    {
        _moving = false;
        _firstSyncExpected = false;
    }

    public StatusMoment Next(StatusSnapshot s)
    {
        if (s.Status == RunnerStatus.Syncing)
        {
            _moving |= s.IsTransferring || s.FilesTotal > 0;
            return StatusMoment.None;
        }
        var moved = _moving;
        _moving = false; // any other status ends the pass
        if (s.Status != RunnerStatus.Idle || s.IsWaiting)
            return StatusMoment.None;
        if (_firstSyncExpected && s.LastSyncAt is not null)
        {
            _firstSyncExpected = false;
            return StatusMoment.FirstSync;
        }
        return moved ? StatusMoment.SyncFinished : StatusMoment.None;
    }
}
