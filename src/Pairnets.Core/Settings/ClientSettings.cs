using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pairnets.Core.Settings;

/// <summary>Persisted client settings (%AppData%\Pairnets\settings.json). The token is stored only protected.</summary>
public sealed class ClientSettings
{
    public string? ServerUrl { get; set; }

    /// <summary>
    /// The token, encrypted by an <see cref="ISecretProtector"/> (DPAPI on Windows). Never plain text. It is this
    /// computer's own key when <see cref="DeviceId"/> is set, otherwise the shared token typed in Settings.
    /// </summary>
    public string? ProtectedToken { get; set; }

    /// <summary>
    /// The nest's id for this computer once it has its own key (signed in, or switched over from the
    /// shared token); null while it uses the shared token.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>True when this computer has its own key (it can sign out, and can be removed on the nest).</summary>
    public bool HasOwnKey => !string.IsNullOrEmpty(DeviceId);

    public string? Folder { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>
    /// The person answered the one-time offer to use this computer's own name instead of a placeholder ("PC-1"), either
    /// way (<see cref="Client.ComputerName.WorthOffering"/>); it is not offered again.
    /// </summary>
    public bool NameOfferAnswered { get; set; }

    /// <summary>
    /// The Pairnets account this computer signed in with ("you@example.com"), for showing; null when it signed in on its
    /// nest's own website, or uses the shared token. The account's token itself is never kept.
    /// </summary>
    public string? AccountEmail { get; set; }

    public List<string> ExtraIgnore { get; set; } = [];

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    public bool Paused { get; set; }

    /// <summary>The main window's left side is folded into the dock (page buttons and computers); false = open.</summary>
    public bool SideFolded { get; set; }

    /// <summary>Look for a new Pairnets version at start-up and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Which one-time changes (<see cref="SettingsUpgrades"/>) these settings already had. New settings start current; a
    /// settings file from before the number existed reads as 0 (<see cref="SettingsStore.Load(string, Action{string}?)"/>).
    /// </summary>
    public int SettingsVersion { get; set; } = SettingsUpgrades.Current;

    /// <summary>While another computer uploads a big batch, download it in one go when it's done.</summary>
    public bool WaitForPeerBatches { get; set; } = true;

    /// <summary>
    /// Pairnets in the file manager: Explorer's left side, the right-click items and the folder icon on Windows; the
    /// sidebar bookmark, right-click items and folder icon on Linux; the folder icon on a Mac.
    /// </summary>
    public bool ShowInFileManager { get; set; } = true;

    /// <summary>
    /// Windows only, opt-in: the Pairnets folder as a Windows cloud folder (like OneDrive), with a tick, sync arrows or a
    /// cloud on every file, and with <see cref="CloudFolderMode.OnDemand"/> files that stay on the nest until opened.
    /// </summary>
    public CloudFolderMode CloudFolder { get; set; } = CloudFolderMode.Off;

    /// <summary>Show the speed (MB/s) on the lines of the overview's picture while files move.</summary>
    public bool ShowMapSpeed { get; set; } = true;

    /// <summary>Files transferred at the same time: 1, 2, 4 or 8.</summary>
    public int ParallelTransfers { get; set; } = 4;

    /// <summary>Upload speed limit in MB/s; null or 0 = no limit.</summary>
    public double? UploadLimitMBps { get; set; }

    /// <summary>Download speed limit in MB/s; null or 0 = no limit.</summary>
    public double? DownloadLimitMBps { get; set; }

    /// <summary>Update the server without asking whenever this app finds it out of date.</summary>
    public bool AutoUpdateServer { get; set; }

    /// <summary>A server version the user chose not to be reminded about ("Don't ask for this version").</summary>
    public string? SkippedServerVersion { get; set; }

    /// <summary>Debug mode: more detail in the log, and the server's update log in the update window.</summary>
    public bool DebugMode { get; set; }

    /// <summary>Show a notification when another computer wants to join the nest.</summary>
    public bool NotifyJoinRequests { get; set; } = true;

    /// <summary>Show a notification when something needs the person (conflicts, blocked syncs, sign-in problems).</summary>
    public bool NotifyAttention { get; set; } = true;

    /// <summary>Show a notification when a new Pairnets version is out or the server was updated.</summary>
    public bool NotifyUpdates { get; set; } = true;

    /// <summary><see cref="ParallelTransfers"/> limited to the offered choices.</summary>
    public int EffectiveParallelTransfers => ParallelTransfers is 1 or 2 or 4 or 8 ? ParallelTransfers : 4;

    /// <summary>"Limited to 5 MB/s", "Limited to 5 MB/s up, 10 MB/s down", or null.</summary>
    public string? LimitText
    {
        get
        {
            var up = UploadLimitMBps is > 0 ? UploadLimitMBps : null;
            var down = DownloadLimitMBps is > 0 ? DownloadLimitMBps : null;
            if (up is null && down is null)
                return null;
            if (up == down)
                return "Limited to " + Client.Format.Megabytes(up!.Value);
            var parts = new List<string>();
            if (up is not null)
                parts.Add(Client.Format.Megabytes(up.Value) + " up");
            if (down is not null)
                parts.Add(Client.Format.Megabytes(down.Value) + " down");
            return "Limited to " + string.Join(", ", parts);
        }
    }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ProtectedToken)
        && !string.IsNullOrWhiteSpace(Folder) && !string.IsNullOrWhiteSpace(DeviceName);

    /// <summary>A copy to change without touching the settings a running session uses.</summary>
    public ClientSettings Clone()
    {
        var copy = (ClientSettings)MemberwiseClone();
        copy.ExtraIgnore = [.. ExtraIgnore];
        return copy;
    }
}

/// <summary>How the Pairnets folder works with Windows' cloud files (see <see cref="ClientSettings.CloudFolder"/>).</summary>
public enum CloudFolderMode
{
    /// <summary>A plain folder, as always.</summary>
    Off,

    /// <summary>A cloud folder with every file kept on this PC: ticks and sync arrows in Explorer.</summary>
    KeepAllHere,

    /// <summary>Files On-Demand: files from other computers stay on the nest until opened ("Free up space" works).</summary>
    OnDemand,
}

/// <summary>Encrypts secrets at rest. The Windows client implements this with DPAPI (current user).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);

    /// <summary>
    /// Removes the secret that <see cref="Protect"/> saved, from wherever it is kept (Keychain, keyring, private file).
    /// Nothing to do when the secret lives inside the settings file itself (DPAPI on Windows).
    /// </summary>
    void Forget(string protectedText)
    {
    }
}

/// <summary>
/// Loads and atomically saves <see cref="ClientSettings"/>. Every save also leaves a copy of what it wrote
/// (settings.json.bak); when settings.json is gone or unreadable, the copy is used, so the sign-in, the computer's name
/// and the switches are not lost to one bad file.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string BackupSuffix = ".bak";

    public static string DefaultPath => Path.Combine(PairnetsPaths.RoamingDir, "settings.json");

    public static ClientSettings Load(string path) => Load(path, null);

    /// <param name="report">Told, in plain words for the log, when the file was missing or unreadable and what was used instead.</param>
    public static ClientSettings Load(string path, Action<string>? report)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(path))
        {
            if (TryRead(backup) is not { } copy)
                return new ClientSettings(); // never set up on this computer (or reset)
            report?.Invoke($"{Path.GetFileName(path)} was missing; using the safety copy saved with the last change.");
            TryRestore(backup, path, report);
            return copy;
        }
        try
        {
            return Parse(File.ReadAllText(path)) ?? new ClientSettings();
        }
        catch (JsonException ex)
        {
            // Keep the unreadable file for inspection and use the safety copy, or start fresh rather than crash.
            File.Copy(path, path + ".corrupt", overwrite: true);
            if (TryRead(backup) is { } copy)
            {
                report?.Invoke($"{Path.GetFileName(path)} could not be read ({ex.Message}); kept it as .corrupt and used the safety copy.");
                TryRestore(backup, path, report);
                return copy;
            }
            report?.Invoke($"{Path.GetFileName(path)} could not be read ({ex.Message}) and there is no safety copy; kept it as .corrupt and started with empty settings.");
            return new ClientSettings();
        }
    }

    public static void Save(string path, ClientSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        try
        {
            File.Copy(tmp, path + BackupSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The safety copy is extra; the save itself goes on.
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads settings JSON. A file written before <see cref="ClientSettings.SettingsVersion"/> existed reads as version 0,
    /// so its one-time changes (<see cref="SettingsUpgrades"/>) still run.
    /// </summary>
    private static ClientSettings? Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<ClientSettings>(json, Json);
        if (settings is null)
            return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.TryGetProperty("settingsVersion", out _))
            settings.SettingsVersion = 0;
        return settings;
    }

    private static ClientSettings? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryRestore(string backup, string path, Action<string>? report)
    {
        try
        {
            File.Copy(backup, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke($"The safety copy could not be put back as {Path.GetFileName(path)} ({ex.Message}); it is used for now.");
        }
    }
}

/// <summary>
/// One-time changes to settings saved by an older Pairnets, applied once at start (<see cref="ClientSettings.SettingsVersion"/>).
/// </summary>
public static class SettingsUpgrades
{
    /// <summary>
    /// 1 (1.0.60): "Check for updates" is switched on once for everyone. Copies left with it off (test set-ups) never
    /// heard of new versions and had to be updated by running the installer by hand. Switched off again later, it stays off.
    /// </summary>
    public const int Current = 1;

    /// <summary>Applies what <paramref name="settings"/> did not have yet; returns a line for the log per change (empty: nothing to save).</summary>
    public static IReadOnlyList<string> Apply(ClientSettings settings)
    {
        var done = new List<string>();
        if (settings.SettingsVersion < 1)
        {
            if (!settings.CheckForUpdates)
                done.Add("\"Check for updates\" was off; switched it on once, so this computer hears about new versions again.");
            settings.CheckForUpdates = true;
        }
        if (settings.SettingsVersion < Current)
        {
            settings.SettingsVersion = Current;
            done.Add($"Settings brought up to version {Current}.");
        }
        return done;
    }
}

/// <summary>Where the client keeps its files.</summary>
public static class PairnetsPaths
{
    /// <summary>The app's folder name under %AppData% and %LocalAppData%.</summary>
    public const string FolderName = "Pairnets";

    /// <summary>%AppData%\Pairnets (settings).</summary>
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>%LocalAppData%\Pairnets (state databases, logs).</summary>
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>State directory for a sync folder: %LocalAppData%\Pairnets\&lt;hash of folder path&gt;.</summary>
    public static string StateDirFor(string folder, string? baseDir = null)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return Path.Combine(baseDir ?? LocalDir, hash);
    }
}
