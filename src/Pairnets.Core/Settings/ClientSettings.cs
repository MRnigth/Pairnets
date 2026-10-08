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

    public List<string> ExtraIgnore { get; set; } = [];

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    public bool Paused { get; set; }

    /// <summary>Look for a new Pairnets version at start-up and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>While another computer uploads a big batch, download it in one go when it's done.</summary>
    public bool WaitForPeerBatches { get; set; } = true;

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

/// <summary>Loads and atomically saves <see cref="ClientSettings"/>.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(PairnetsPaths.RoamingDir, "settings.json");

    public static ClientSettings Load(string path)
    {
        if (!File.Exists(path))
            return new ClientSettings();
        try
        {
            return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), Json) ?? new ClientSettings();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start fresh rather than crash.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return new ClientSettings();
        }
    }

    public static void Save(string path, ClientSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
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
