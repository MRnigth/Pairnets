using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tether.Core.Settings;

/// <summary>Persisted client settings (%AppData%\Tether\settings.json). The token is stored only protected.</summary>
public sealed class ClientSettings
{
    public string? ServerUrl { get; set; }

    /// <summary>The token, encrypted by an <see cref="ISecretProtector"/> (DPAPI on Windows). Never plain text.</summary>
    public string? ProtectedToken { get; set; }

    public string? Folder { get; set; }

    public string? DeviceName { get; set; }

    public List<string> ExtraIgnore { get; set; } = [];

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    public bool Paused { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ProtectedToken)
        && !string.IsNullOrWhiteSpace(Folder) && !string.IsNullOrWhiteSpace(DeviceName);
}

/// <summary>Encrypts secrets at rest. The Windows client implements this with DPAPI (current user).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);
}

/// <summary>Loads and atomically saves <see cref="ClientSettings"/>.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(TetherPaths.RoamingDir, "settings.json");

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
public static class TetherPaths
{
    /// <summary>%AppData%\Tether (settings).</summary>
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");

    /// <summary>%LocalAppData%\Tether (state databases, logs).</summary>
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tether");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>State directory for a sync folder: %LocalAppData%\Tether\&lt;hash of folder path&gt;.</summary>
    public static string StateDirFor(string folder, string? baseDir = null)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return Path.Combine(baseDir ?? LocalDir, hash);
    }
}
