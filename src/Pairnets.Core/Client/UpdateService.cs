namespace Pairnets.Core.Client;

/// <summary>
/// Checks for a newer Pairnets shortly after start-up and then once a day, every 2 hours on a beta (when enabled), and
/// remembers the result for the UI. Failures are silent: no update is simply "none found".
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>How often a computer on a beta looks: betas come out often and their fixes should land the same day.</summary>
    public static readonly TimeSpan BetaInterval = TimeSpan.FromHours(2);

    private readonly UpdateChecker _checker;
    private readonly ReleaseVersion _current;
    private readonly string? _asset;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _interval;
    private readonly DataVersions _data;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private ITimer? _timer;
    private bool _enabled;

    public UpdateService(UpdateChecker checker, string currentVersion, string? asset, TimeProvider? clock = null,
        TimeSpan? firstDelay = null, TimeSpan? interval = null)
        : this(checker, new ReleaseVersion(ReleaseVersion.TryParseNumber(currentVersion, out var n) ? n : new Version(0, 0, 0, 0), ReleaseChannel.Stable, 0),
            asset, clock, firstDelay, interval)
    {
    }

    public UpdateService(UpdateChecker checker, ReleaseVersion current, string? asset, TimeProvider? clock = null,
        TimeSpan? firstDelay = null, TimeSpan? interval = null, DataVersions? data = null)
    {
        _checker = checker;
        _current = current;
        _asset = asset;
        _clock = clock ?? TimeProvider.System;
        _firstDelay = firstDelay ?? TimeSpan.FromSeconds(15);
        _interval = interval ?? TimeSpan.FromHours(24);
        _data = data ?? DataVersions.Current;
        Channel = ReleaseChannel.Normalize(current.Channel);
    }

    public string CurrentVersion => _current.Display;

    /// <summary>The build running now.</summary>
    public ReleaseVersion Current => _current;

    /// <summary>The channel this computer follows (Settings › Updates › Beta updates): stable, or one of the betas.</summary>
    public string Channel { get; private set; }

    /// <summary>The newer release found by the last check, or null.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>A newer build was found but not offered: it could not read this computer's data (see <see cref="DataVersions"/>).</summary>
    public bool HeldBackForData { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>
    /// The channel moved down (Developer beta to Public beta, or a beta to Off) and nothing newer is out on it yet: the
    /// build running now stays until there is. Null otherwise.
    /// </summary>
    public string? Waiting => Available is null && _current.IsBeta && ReleaseChannel.Rank(Channel) < ReleaseChannel.Rank(_current.Channel)
        ? UpdateTexts.WaitingFor(Channel, _current.Display)
        : null;

    /// <summary>Raised when a check finds a newer release (once per build).</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>Raised after every check, found or not (for "checked 19:41").</summary>
    public event Action? Checked;

    /// <summary>Turns the automatic checks on or off ("Check for updates by itself").</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _timer?.Dispose();
        var every = ReleaseChannel.IsBeta(Channel) ? BetaInterval : _interval;
        _timer = enabled ? _clock.CreateTimer(_ => _ = CheckNowAsync(CancellationToken.None), null, _firstDelay, every) : null;
    }

    /// <summary>
    /// Follows another channel (the choice in Settings, or one made on the account page): looks again at once, and keeps
    /// looking as often as that channel needs.
    /// </summary>
    public void SetChannel(string channel)
    {
        channel = ReleaseChannel.Normalize(channel);
        if (channel == Channel)
            return;
        Channel = channel;
        Available = null;
        if (_enabled)
            SetEnabled(true);
        _ = CheckNowAsync(CancellationToken.None);
    }

    public async Task<UpdateInfo?> CheckNowAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _checker.CheckAsync(_current, Channel, _asset, _data, ct).ConfigureAwait(false);
            var found = result.Found;
            LastChecked = _clock.GetUtcNow();
            HeldBackForData = result.HeldBackForData;
            var isNew = found is not null && found.Release != Available?.Release;
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
