using Microsoft.Extensions.Logging;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Desktop.Platform;

/// <summary>Linux desktop: libsecret keyring, gio trash, notify-send, XDG autostart.</summary>
public sealed class LinuxPlatform : IPlatformServices
{
    public string Name => "Linux";

    public ISecretProtector Secrets { get; } = new LinuxSecrets();

    public ILocalTrash CreateTrash(ILogger log) => new GioTrash(log);

    public void Notify(string title, string text) =>
        Proc.Run("notify-send", ["--app-name=Tether", "--icon=tether", title, text], timeoutMs: 3000);

    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "tether.desktop");

    public bool IsAutoStartEnabled() => File.Exists(AutostartFile);

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
            Name=Tether
            Comment=Keep a folder in sync through your own server
            Exec="{Environment.ProcessPath}" --autostart
            Icon=tether
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
        private static readonly string[] Attributes = ["service", "tether", "account", "sync-token"];
        private readonly PrivateFileSecrets _fallback = new(Path.Combine(TetherPaths.LocalDir, "token"));

        public string Protect(string plainText)
        {
            var (code, _) = Proc.Run("secret-tool", ["store", "--label=Tether sync token", .. Attributes], stdin: plainText);
            return code == 0 ? Marker : _fallback.Protect(plainText);
        }

        public string Unprotect(string protectedText)
        {
            if (protectedText == PrivateFileSecrets.Marker)
                return _fallback.Unprotect(protectedText);
            if (protectedText != Marker)
                throw new InvalidOperationException("Unknown token storage.");
            var (code, output) = Proc.Run("secret-tool", ["lookup", .. Attributes]);
            if (code != 0 || output.Length == 0)
                throw new InvalidOperationException("The token is not in the keyring (is it unlocked?).");
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
