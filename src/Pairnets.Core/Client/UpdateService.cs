namespace Pairnets.Core.Client;

/// <summary>
/// Checks for a newer Pairnets shortly after start-up and then once a day (when enabled), and
/// remembers the result for the UI. Failures are silent: no update is simply "none found".
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly UpdateChecker _checker;
    private readonly string _currentVersion;
    private readonly string? _asset;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private ITimer? _timer;

    public UpdateService(UpdateChecker checker, string currentVersion, string? asset, TimeProvider? clock = null,
        TimeSpan? firstDelay = null, TimeSpan? interval = null)
    {
        _checker = checker;
        _currentVersion = currentVersion;
        _asset = asset;
        _clock = clock ?? TimeProvider.System;
        _firstDelay = firstDelay ?? TimeSpan.FromSeconds(15);
        _interval = interval ?? TimeSpan.FromHours(24);
    }

    public string CurrentVersion => _currentVersion;

    /// <summary>The newer release found by the last check, or null.</summary>
    public UpdateInfo? Available { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Raised when a check finds a newer release (once per version).</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>Raised after every check, found or not (for "checked 19:41").</summary>
    public event Action? Checked;

    /// <summary>Turns the automatic checks on or off ("Check for updates when Pairnets starts").</summary>
    public void SetEnabled(bool enabled)
    {
        _timer?.Dispose();
        _timer = enabled ? _clock.CreateTimer(_ => _ = CheckNowAsync(CancellationToken.None), null, _firstDelay, _interval) : null;
    }

    public async Task<UpdateInfo?> CheckNowAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = await _checker.CheckAsync(_currentVersion, _asset, ct).ConfigureAwait(false);
            LastChecked = _clock.GetUtcNow();
            var isNew = found is not null && found.Version != Available?.Version;
            Available = found;
            Checked?.Invoke();
            if (isNew)
                UpdateAvailable?.Invoke(found!);
            return found;
        }
        finally
        {
            _busy.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _busy.Dispose();
    }
}
