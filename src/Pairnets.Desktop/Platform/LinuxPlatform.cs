using Microsoft.Extensions.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Platform;

/// <summary>Linux desktop: libsecret keyring, gio trash, notify-send, XDG autostart.</summary>
public sealed class LinuxPlatform : IPlatformServices
{
    public string Name => "Linux";

    public ISecretProtector Secrets { get; } = new LinuxSecrets();

    public ILocalTrash CreateTrash(ILogger log) => new GioTrash(log);

    public void Notify(string title, string text) =>
        Proc.Run("notify-send", ["--app-name=Pairnets", "--icon=pairnets", title, text], timeoutMs: 3000);

    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "pairnets.desktop");

    public bool IsAutoStartEnabled() => File.Exists(AutostartFile);

    public bool RemoveLegacyAutoStart()
    {
        var legacy = Path.Combine(Path.GetDirectoryName(AutostartFile)!, Pairnets.Core.Legacy.TetherNames.LinuxAutostartFile);
        if (!File.Exists(legacy))
            return false;
        File.Delete(legacy);
        return true;
    }

    public void SetAutoStart(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(AutostartFile))
                File.Delete(AutostartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        File.WriteAllText(AutostartFile, $"""
            [Desktop Entry]
            Type=Application
            Name=Pairnets
            Comment=Keep a folder in sync through your own server
            Exec="{Environment.ProcessPath}" --autostart
            Icon=pairnets
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """);
    }

    public void Open(string path) => Proc.Start("xdg-open", path);

    public void Reveal(string path) => Open(File.Exists(path) ? Path.GetDirectoryName(path)! : path);

    /// <summary>Token in the desktop keyring via secret-tool (reads the secret from stdin), else a 600 file.</summary>
    private sealed class LinuxSecrets : ISecretProtector
    {
        private const string Marker = "libsecret:v1";
        private static readonly string[] Attributes = ["service", "pairnets", "account", "sync-token"];
        private static readonly string[] TetherAttributes = ["service", Pairnets.Core.Legacy.TetherNames.SecretToolService, "account", "sync-token"];
        private readonly PrivateFileSecrets _fallback = new(Path.Combine(PairnetsPaths.LocalDir, "token"));

        public string Protect(string plainText)
        {
            var (code, _) = Proc.Run("secret-tool", ["store", "--label=Pairnets sync token", .. Attributes], stdin: plainText);
            return code == 0 ? Marker : _fallback.Protect(plainText);
        }

        public string Unprotect(string protectedText)
        {
            if (protectedText == PrivateFileSecrets.Marker)
                return _fallback.Unprotect(protectedText);
            if (protectedText != Marker)
                throw new InvalidOperationException("Unknown token storage.");
            var (code, output) = Proc.Run("secret-tool", ["lookup", .. Attributes]);
            if (code == 0 && output.Length > 0)
                return output.Trim();
            // Saved by Tether: copy it to Pairnets' entry.
            (code, output) = Proc.Run("secret-tool", ["lookup", .. TetherAttributes]);
            if (code != 0 || output.Length == 0)
                throw new InvalidOperationException("The token is not in the keyring (is it unlocked?).");
            Protect(output.Trim());
            return output.Trim();
        }
    }

    private sealed class GioTrash(ILogger log) : ILocalTrash
    {
        public void Delete(string fullPath)
        {
            var (code, _) = Proc.Run("gio", ["trash", "--", fullPath]);
            if (code == 0 && !File.Exists(fullPath))
                return;
            log.LogWarning("Moving {Path} to the trash failed; deleting permanently (the server keeps history)", fullPath);
            File.Delete(fullPath);
        }
    }
}
