using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>A file being uploaded or downloaded right now (several run at once).</summary>
public sealed record ActiveTransfer(string Path, string Operation, long BytesDone, long BytesTotal)
{
    public string FileName => Paths.PathRules.FileName(Path);

    public int? Percent => BytesTotal > 0 ? (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100) : null;

    public string PercentText => Percent is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : string.Empty;

    public bool IsUpload => Operation == "upload";

    public bool HasPercent => Percent is not null;

    public double PercentValue => Percent ?? 0;
}

/// <summary>A computer asking to join the nest, as the other computers hear about it.</summary>
public sealed record JoinRequest(string Code, string Name, string System, DateTimeOffset At)
{
    /// <summary>"LAPTOP-2 (Windows) wants to join".</summary>
    public string Title => System.Length > 0 ? $"{Name} ({System}) wants to join" : $"{Name} wants to join";
}

/// <summary>Another computer's latest live report (<see cref="TransferReport"/>) and when it arrived here.</summary>
public sealed record PeerTransfer(TransferReport Report, DateTimeOffset At);

/// <summary>Everything a UI needs to draw the current state, as one immutable value.</summary>
public sealed record StatusSnapshot(
    RunnerStatus Status,
    string Text,
    DateTimeOffset? LastSyncAt,
    string? CurrentPath,
    string? Operation,
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    BlockReason BlockReason,
    int PendingDeletes,
    int Warnings,
    bool Paused)
{
    public static StatusSnapshot Initial { get; } =
        new(RunnerStatus.Offline, "Starting…", null, null, null, 0, 0, 0, 0, BlockReason.None, 0, 0, false);

    // ---- transfers: several files at once, speed and time left

    /// <summary>The uploads and downloads running right now.</summary>
    public IReadOnlyList<ActiveTransfer> Active { get; init; } = [];

    /// <summary>Bytes moved and planned in this sync (several passes in a row count as one).</summary>
    public long PassBytesDone { get; init; }

    public long PassBytesTotal { get; init; }

    /// <summary>Recent transfer speed (about the last 5 seconds), both directions together.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Recent upload speed of this computer (about the last 5 seconds).</summary>
    public double UpBytesPerSecond { get; init; }

    /// <summary>Recent download speed of this computer (about the last 5 seconds).</summary>
    public double DownBytesPerSecond { get; init; }

    /// <summary>When this computer last really moved a byte (up or down), or null.</summary>
    public DateTimeOffset? LastBytesAt { get; init; }

    /// <summary>
    /// Files are in flight but no byte moved for <see cref="TransferReport.StallAfter"/>, or requests are held by "too
    /// many requests" (refreshed about once a second).
    /// </summary>
    public bool Stalled { get; init; }

    /// <summary>Whether files in flight are stuck at <paramref name="now"/>: no byte since <see cref="LastBytesAt"/> for a while, or held.</summary>
    public bool StalledAt(DateTimeOffset now) =>
        Status == RunnerStatus.Syncing && Active.Count > 0
        && (IsSlowedDown || LastBytesAt is not { } last || now - last >= TransferReport.StallAfter);

    // ---- what a pass is doing now

    /// <summary>The running pass's stage (<see cref="SyncStage.None"/> when no pass runs).</summary>
    public SyncStage Stage { get; init; }

    /// <summary>While checking the folder: files checked so far, of <see cref="CheckTotal"/> (0 while still listing).</summary>
    public int CheckedFiles { get; init; }

    public int CheckTotal { get; init; }

    // ---- slowed down by the server ("too many requests")

    /// <summary>When requests go on again after a 429 "too many requests", or null.</summary>
    public DateTimeOffset? SlowedUntil { get; init; }

    /// <summary>Who said "too many requests": the server's host name ("sync.pairnets.app").</summary>
    public string? SlowedBy { get; init; }

    /// <summary>True while syncing waits because the server asked for fewer requests.</summary>
    public bool IsSlowedDown => SlowedUntil is not null && Status == RunnerStatus.Syncing;

    /// <summary>"Too many requests to sync.pairnets.app; continuing in 30 s".</summary>
    public string SlowedText(DateTimeOffset now)
    {
        var seconds = SlowedUntil is { } until ? Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)) : 1;
        return $"Too many requests to {SlowedBy ?? "the server"}; continuing in {seconds} s";
    }

    /// <summary>Nothing to do but wait: for another computer's batch, or for the server to take requests again.</summary>
    public bool IsHeldUp => IsWaiting || IsSlowedDown;

    // ---- live speeds of every computer (pushed by the server, never guessed)

    /// <summary>A report older than this counts as "nothing moving" (its computer stopped sending).</summary>
    public static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(10);

    /// <summary>The other computers' latest live reports, by name.</summary>
    public IReadOnlyDictionary<string, PeerTransfer> Peers { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, PeerTransfer>.Empty;

    /// <summary>What this computer is moving right now (the report it sends), or null when it moves nothing.</summary>
    public TransferReport? OwnLive =>
        Status == RunnerStatus.Syncing && (Stage == SyncStage.Transferring || Active.Count > 0)
        && new TransferReport(UpBytesPerSecond, DownBytesPerSecond, FilesDone, FilesTotal, PassBytesDone, PassBytesTotal,
            Uploading: Active.Any(a => a.IsUpload), Downloading: Active.Any(a => !a.IsUpload), Stalled: Stalled && Active.Count > 0) is { IsActive: true } own
            ? own
            : null;

    /// <summary>
    /// What a computer is moving right now, by its name: this computer's own numbers, or the other's last report if it
    /// is fresh. Null when it moves nothing (or said nothing for <see cref="LiveFor"/>).
    /// </summary>
    public TransferReport? LiveOf(string? name, string? thisDevice, DateTimeOffset now)
    {
        if (name is null)
            return null;
        if (string.Equals(name, thisDevice, StringComparison.OrdinalIgnoreCase))
            return OwnLive;
        foreach (var (device, peer) in Peers)
        {
            if (string.Equals(device, name, StringComparison.OrdinalIgnoreCase))
                return now - peer.At <= LiveFor && peer.Report.IsActive ? peer.Report : null;
        }
        return null;
    }

    /// <summary>"Limited to 5 MB/s" (or "Limited to 5 MB/s up, 10 MB/s down"), or null without limits.</summary>
    public string? LimitText { get; init; }

    /// <summary>"Uploading 120 files", "Downloading 32 files" or "Syncing 50 files".</summary>
    public string BatchTitle => FilesTotal <= 1 && Active.Count <= 1
        ? $"{OperationText} {CurrentFileName}"
        : $"{(Active.Count > 0 && Active.All(a => a.Operation == Active[0].Operation) ? OperationText : "Syncing")} {Format.Count(FilesTotal)} files";

    /// <summary>"12.4 MB/s · about 2 min left" (empty until the speed is known).</summary>
    public string SpeedText
    {
        get
        {
            if (BytesPerSecond < 1)
                return string.Empty;
            var text = Format.Speed(BytesPerSecond);
            var left = PassBytesTotal - PassBytesDone;
            if (left > 0)
                text += " · " + Format.Duration(TimeSpan.FromSeconds(left / BytesPerSecond)) + " left";
            return text;
        }
    }

    /// <summary>0–100 over the whole sync by bytes (falls back to files), or null.</summary>
    public int? OverallPercent => PassBytesTotal > 0
        ? (int)Math.Clamp(PassBytesDone * 100 / PassBytesTotal, 0, 100)
        : FilesTotal > 0 ? (int)Math.Clamp(FilesDone * 100L / FilesTotal, 0, 100) : null;

    /// <summary>"37 of 120 files · 412 MB of 1.30 GB".</summary>
    public string OverallText => FilesTotal == 0 ? string.Empty
        : $"{Format.Count(Math.Min(FilesDone, FilesTotal))} of {Format.Count(FilesTotal)} files"
          + (PassBytesTotal > 0 ? $" · {Format.Bytes(Math.Min(PassBytesDone, PassBytesTotal))} of {Format.Bytes(PassBytesTotal)}" : string.Empty);

    // ---- waiting for another computer's big batch

    /// <summary>The other computer's big upload this one waits for, or null.</summary>
    public PeerWait? WaitingFor { get; init; }

    /// <summary>
    /// True while waiting and nothing else is going on (the status card then shows the wait). Checking the folder and
    /// reading the server's list are said as they are, also during a wait.
    /// </summary>
    public bool IsWaiting => WaitingFor is not null && Status is RunnerStatus.Idle or RunnerStatus.Syncing && !IsTransferring
        && Stage is not (SyncStage.Checking or SyncStage.ReadingServer);

    /// <summary>
    /// How far the other computer's batch is: its own live report when it sends one (files done of its batch), otherwise
    /// the changes that reached the server so far of the files it announced.
    /// </summary>
    public (int Done, int Total)? WaitingCounts(DateTimeOffset now) =>
        WaitingFor is not { } w ? null
        : LiveOf(w.Device, null, now) is { FilesTotal: > 0 } live ? (Math.Min(live.FilesDone, live.FilesTotal), live.FilesTotal)
        : (Math.Min(w.Seen, w.Count), w.Count);

    /// <summary>"383 of 38,206 files are on the server · ↑ 3.10 MB/s".</summary>
    public string WaitingProgressText => WaitingProgressAt(DateTimeOffset.UtcNow);

    /// <inheritdoc cref="WaitingProgressText"/>
    public string WaitingProgressAt(DateTimeOffset now)
    {
        if (WaitingCounts(now) is not var (done, total))
            return string.Empty;
        var text = $"{Format.Count(done)} of {Format.Count(total)} files are on the server";
        var flow = LiveOf(WaitingFor!.Device, null, now) is { } live ? Format.Flow(live.UpBytesPerSecond, live.DownBytesPerSecond) : string.Empty;
        return flow.Length > 0 ? text + " · " + flow : text;
    }

    public int? WaitingPercent => WaitingCounts(DateTimeOffset.UtcNow) is var (done, total) && total > 0
        ? (int)Math.Clamp(done * 100L / total, 0, 100)
        : null;

    // ---- the other computers

    /// <summary>
    /// The computers that use the server, this one included (the Devices page and the overview's
    /// picture), or null before the first answer; empty from a server too old to say (<see cref="DevicesUnsupported"/>).
    /// </summary>
    public IReadOnlyList<DeviceInfo>? Devices { get; init; }

    /// <summary>True when the server answered but is too old to list the computers that use it.</summary>
    public bool DevicesUnsupported { get; init; }

    /// <summary>When a change last arrived from each other computer (by device name).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> HeardFrom { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, DateTimeOffset>.Empty;

    // ---- the nest's website and computers asking to join

    /// <summary>The nest's website ("https://nest.pairnets.app"), or null when it has none (or before it answered).</summary>
    public string? NestUrl { get; init; }

    /// <summary>Computers waiting to be approved on the nest (heard on the push channel, newest first).</summary>
    public IReadOnlyList<JoinRequest> JoinRequests { get; init; } = [];

    /// <summary>The page where a join request is approved, or null without a website.</summary>
    public string? ReviewUrl(JoinRequest request) => NestUrl is { } url ? $"{url}/link?code={Uri.EscapeDataString(request.Code)}" : null;

    // ---- signed in with a Pairnets account

    /// <summary>
    /// True when this computer reaches its server through the Pairnets service (https://sync.pairnets.app/n/&lt;nest id&gt;/),
    /// having signed in with a Pairnets account. Its computers are then managed on the account page (see <see cref="Relay"/>).
    /// </summary>
    public bool IsRelay { get; init; }

    /// <summary>The Pairnets account this computer signed in with ("you@example.com"), or null.</summary>
    public string? AccountEmail { get; init; }

    // ---- the server

    /// <summary>Last known server info (version, free space, updater), or null before the first answer.</summary>
    public ServerInfo? Server { get; init; }

    /// <summary>"412 GB free on server" or null when the server does not say.</summary>
    /// <summary>True once the server has answered and the connection is not currently down.</summary>
    public bool IsConnected => Status != RunnerStatus.Offline && (Server is not null || LastSyncAt is not null);

    /// <summary>"Connected", "Not connected", or null while the first connection attempt is still running.</summary>
    public string? ConnectionText => IsConnected ? "Connected"
        : Status == RunnerStatus.Offline && Text.StartsWith("Offline", StringComparison.Ordinal) ? "Not connected"
        : null;

    /// <summary>
    /// "Server 1.0.58"; "Server: old version" for a server too old to say which version it runs;
    /// null before the server has answered.
    /// </summary>
    public string? ServerVersionText => Server is null ? null
        : Server.ServerVersion is { Length: > 0 } v ? "Server " + v
        : "Server: old version";

    /// <summary>True when the server runs an older release than this app (or is too old to say).</summary>
    public bool ServerIsOlder => Server is not null
        && (Server.ServerVersion is not { Length: > 0 } v || UpdateChecker.ServerIsOlder(v, PairnetsInfo.ProductVersion));

    /// <summary>The label of the button next to the server version.</summary>
    public string ServerUpdateButtonText => ServerIsOlder ? "Update server…" : "Check for update";

    public string? ServerFreeText => Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " free on server" : null;

    /// <summary>Less than 5 GB or 5 % left on the server's disk.</summary>
    public bool ServerSpaceLow => Server is { DiskFreeBytes: { } free, DiskTotalBytes: { } total }
        && (free < 5L * 1024 * 1024 * 1024 || (total > 0 && free * 20 < total));

    /// <summary>0–100 for the current file, or null when unknown (no size yet / not transferring).</summary>
    public int? Percent => CurrentPath is not null && BytesTotal > 0 ? (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100) : null;

    public bool IsTransferring => Status == RunnerStatus.Syncing && CurrentPath is not null;

    /// <summary>"64.0 MB of 100 MB · 64%" (or just the size while the total is unknown).</summary>
    public string ProgressText => BytesTotal > 0
        ? $"{Format.Bytes(BytesDone)} of {Format.Bytes(BytesTotal)} · {Percent}%"
        : BytesDone > 0 ? Format.Bytes(BytesDone) : string.Empty;

    /// <summary>"File 3 of 5".</summary>
    public string FileCountText => FilesTotal > 0 ? $"File {Math.Min(FilesDone + 1, FilesTotal)} of {FilesTotal}" : string.Empty;

    /// <summary>File name of the current transfer.</summary>
    public string? CurrentFileName => CurrentPath is null ? null : Paths.PathRules.FileName(CurrentPath);

    /// <summary>Folder of the current transfer ("" for the top level).</summary>
    public string CurrentFolder => CurrentPath is null ? string.Empty : Paths.PathRules.Parent(CurrentPath) ?? string.Empty;

    /// <summary>"Uploading", "Downloading", "Deleting" or "Working on".</summary>
    public string OperationText => Operation switch
    {
        "upload" => "Uploading",
        "download" => "Downloading",
        "delete" => "Deleting",
        _ => "Working on",
    };

    public string LastSyncText => LastSyncAt is { } at ? "Last synced " + at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "Not synced yet";

    /// <summary>"Uploading", "Downloading" or "Uploading and downloading" for the transfers running now, or null.</summary>
    private string? Direction
    {
        get
        {
            if (Active.Count > 0)
                return Active.All(a => a.IsUpload) ? "Uploading" : Active.All(a => !a.IsUpload) ? "Downloading" : "Uploading and downloading";
            return Operation switch
            {
                "upload" => "Uploading",
                "download" => "Downloading",
                _ => null,
            };
        }
    }

    /// <summary>"37 of 120 files · 4.90 MB/s · about 3 min left" (what the transfers have done and how fast).</summary>
    private string TransferSummary
    {
        get
        {
            var files = FilesTotal > 0 ? $"{Format.Count(Math.Min(FilesDone, FilesTotal))} of {Format.Count(FilesTotal)} files" : string.Empty;
            var speed = SpeedText;
            return files.Length > 0 && speed.Length > 0 ? files + " · " + speed : files + speed;
        }
    }

    /// <summary>The title of the status card: what Pairnets is doing, in a few words.</summary>
    public string Headline => Status == RunnerStatus.Paused ? "Paused"
        : IsSlowedDown ? "Slowed down by the server"
        : IsWaiting ? $"Waiting for {WaitingFor!.Device} to finish uploading"
        : Status switch
        {
            RunnerStatus.Idle => "Up to date",
            RunnerStatus.Syncing when Stage == SyncStage.Checking => "Checking files",
            RunnerStatus.Syncing when Stage == SyncStage.ReadingServer => "Reading the server's list",
            RunnerStatus.Syncing when IsTransferring && Direction is { } direction => direction,
            RunnerStatus.Syncing => "Syncing…",
            RunnerStatus.Offline => "Offline",
            RunnerStatus.Blocked when BlockReason == BlockReason.SignedOut => "Signed out of your nest",
            RunnerStatus.Blocked when BlockReason == BlockReason.SignInRequired => "Sign in to your nest",
            RunnerStatus.Blocked => "Needs your decision",
            _ => "Problem",
        };

    /// <summary>Extra detail under the headline, without repeating it ("Offline: timeout" → "timeout").</summary>
    public string DetailText => DetailAt(DateTimeOffset.UtcNow);

    /// <inheritdoc cref="DetailText"/>
    public string DetailAt(DateTimeOffset now)
    {
        if (Status != RunnerStatus.Paused)
        {
            if (IsSlowedDown)
                return SlowedText(now);
            if (IsWaiting)
                return "Pairnets downloads the whole batch in one go when it's done, so the two computers don't fight over the connection.";
            if (Status == RunnerStatus.Syncing && Stage == SyncStage.Checking)
                return CheckTotal > 0 ? $"{Format.Count(CheckedFiles)} of {Format.Count(CheckTotal)}" : "Listing the files in your folder";
            if (Status == RunnerStatus.Syncing && Stage == SyncStage.ReadingServer)
                return string.Empty;
            if (IsTransferring && Direction is not null)
                return TransferSummary;
        }
        var root = Headline.TrimEnd('…', '.');
        var text = Text.Trim();
        if (text.Length == 0 || string.Equals(text.TrimEnd('…', '.'), root, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (text.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase))
            return text[(root.Length + 1)..].Trim();
        return text;
    }

    /// <summary>
    /// The status in one line (the tray icon's tip, the tray menu): "Checking files · 12,000 of 38,206",
    /// "Waiting for PC-1 to finish uploading · 383 of 38,206 files", "Uploading · 37 of 120 files · 4.90 MB/s · about 3 min left",
    /// "Too many requests to sync.pairnets.app; continuing in 30 s", "Paused", or the runner's own words.
    /// </summary>
    public string StatusLine => StatusLineAt(DateTimeOffset.UtcNow);

    /// <inheritdoc cref="StatusLine"/>
    public string StatusLineAt(DateTimeOffset now)
    {
        if (Status == RunnerStatus.Paused)
            return "Paused";
        if (IsSlowedDown)
            return SlowedText(now);
        if (IsWaiting && WaitingCounts(now) is var (done, total))
            return $"Waiting for {WaitingFor!.Device} to finish uploading · {Format.Count(done)} of {Format.Count(total)} files";
        if (Status == RunnerStatus.Syncing && Stage == SyncStage.Checking)
            return CheckTotal > 0 ? $"Checking files · {Format.Count(CheckedFiles)} of {Format.Count(CheckTotal)}" : "Checking files";
        if (Status == RunnerStatus.Syncing && Stage == SyncStage.ReadingServer)
            return "Reading the server's list";
        if (IsTransferring && Direction is { } direction)
            return TransferSummary is { Length: > 0 } summary ? direction + " · " + summary : direction;
        return Text;
    }

    /// <summary>The action the UI should offer for a blocked pass, or null.</summary>
    public string? FixLabel => Status != RunnerStatus.Blocked ? null : BlockReason switch
    {
        BlockReason.MassDelete or BlockReason.FolderEmpty => $"Allow these deletions ({PendingDeletes})…",
        BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => "Locate the sync folder…",
        BlockReason.ForeignMarker => "Confirm this folder…",
        BlockReason.ServerChanged or BlockReason.ServerRolledBack => "Re-link to this server…",
        BlockReason.SignedOut => "Sign in again…",
        BlockReason.SignInRequired => "Sign in with your browser…",
        _ => null,
    };
}
