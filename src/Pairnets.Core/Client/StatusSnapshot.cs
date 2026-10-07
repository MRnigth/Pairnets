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

    /// <summary>Recent transfer speed (about the last 5 seconds).</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>"Limited to 5 MB/s" (or "Limited to 5 MB/s up, 10 MB/s down"), or null without limits.</summary>
    public string? LimitText { get; init; }

    /// <summary>"Uploading 120 files", "Downloading 32 files" or "Syncing 50 files".</summary>
    public string BatchTitle => FilesTotal <= 1 && Active.Count <= 1
        ? $"{OperationText} {CurrentFileName}"
        : $"{(Active.Count > 0 && Active.All(a => a.Operation == Active[0].Operation) ? OperationText : "Syncing")} {FilesTotal} files";

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
        : $"{Math.Min(FilesDone, FilesTotal)} of {FilesTotal} files"
          + (PassBytesTotal > 0 ? $" · {Format.Bytes(Math.Min(PassBytesDone, PassBytesTotal))} of {Format.Bytes(PassBytesTotal)}" : string.Empty);

    // ---- waiting for another computer's big batch

    /// <summary>The other computer's big upload this one waits for, or null.</summary>
    public PeerWait? WaitingFor { get; init; }

    /// <summary>True while waiting and nothing else is going on (the status card then shows the wait).</summary>
    public bool IsWaiting => WaitingFor is not null && Status is RunnerStatus.Idle or RunnerStatus.Syncing && !IsTransferring;

    /// <summary>"212 of 340 files are on the server".</summary>
    public string WaitingProgressText => WaitingFor is { } w ? $"{w.Seen} of {w.Count} files are on the server" : string.Empty;

    public int? WaitingPercent => WaitingFor is { Count: > 0 } w ? (int)Math.Clamp(w.Seen * 100L / w.Count, 0, 100) : null;

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

    /// <summary>Short status line shown under the title.</summary>
    public string Headline => IsWaiting ? $"Waiting for {WaitingFor!.Device}" : Status switch
    {
        RunnerStatus.Idle => "Up to date",
        RunnerStatus.Syncing => "Syncing…",
        RunnerStatus.Offline => "Offline",
        RunnerStatus.Paused => "Paused",
        RunnerStatus.Blocked when BlockReason == BlockReason.SignedOut => "Signed out of your nest",
        RunnerStatus.Blocked => "Needs your decision",
        _ => "Problem",
    };

    /// <summary>Extra detail under the headline, without repeating it ("Offline: timeout" → "timeout").</summary>
    public string DetailText
    {
        get
        {
            if (IsWaiting)
                return $"{WaitingFor!.Device} is uploading a big batch. Pairnets downloads it all in one go when it's done, so the two computers don't fight over the connection.";
            var root = Headline.TrimEnd('…', '.');
            var text = Text.Trim();
            if (text.Length == 0 || string.Equals(text.TrimEnd('…', '.'), root, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (text.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase))
                return text[(root.Length + 1)..].Trim();
            return text;
        }
    }

    /// <summary>The action the UI should offer for a blocked pass, or null.</summary>
    public string? FixLabel => Status != RunnerStatus.Blocked ? null : BlockReason switch
    {
        BlockReason.MassDelete or BlockReason.FolderEmpty => $"Allow these deletions ({PendingDeletes})…",
        BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => "Locate the sync folder…",
        BlockReason.ForeignMarker => "Confirm this folder…",
        BlockReason.ServerChanged or BlockReason.ServerRolledBack => "Re-link to this server…",
        BlockReason.SignedOut => "Sign in again…",
        _ => null,
    };
}
