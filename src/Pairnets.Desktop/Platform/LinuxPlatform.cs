using Pairnets.Core.Api;
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

    public bool SetFileManagerEntry(string? folder)
    {
        var files = new LinuxFileManager(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), args => Proc.Run("gio", args).ExitCode);
        return FolderIcon.Follow(folder, (before, now) =>
        {
            if (now is null || Environment.ProcessPath is not { } exe)
                return files.Remove(before) && now is null;
            if (before is { Length: > 0 } && before != now)
                files.Remove(before); // the folder moved: the old one goes back to a plain folder
            return files.Apply(now, exe, FolderIcon.Png);
        });
    }

    /// <summary>GNOME's (and most GTK desktops') "Animations" switch; Windows' when the tests run there.</summary>
    public bool ReduceMotion => OperatingSystem.IsWindows()
        ? WindowsMotion.Reduced()
        : Proc.Run("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"], timeoutMs: 2000) is (0, var output) && output.Trim() == "false";

    /// <summary>
    /// Token in the desktop keyring via secret-tool (reads the secret from stdin), else a 600 file. The first nest's key
    /// is "sync-token" (file "token"); another nest's is "sync-token-&lt;slot&gt;" (file "token-&lt;slot&gt;").
    /// </summary>
    private sealed class LinuxSecrets : ISecretProtector
    {
        private const string Marker = "libsecret:v1";
        private static readonly string[] TetherAttributes = ["service", Pairnets.Core.Legacy.TetherNames.SecretToolService, "account", "sync-token"];

        private static string[] Attributes(string? slot) => ["service", "pairnets", "account", slot is null ? "sync-token" : "sync-token-" + slot];

        private static PrivateFileSecrets Fallback(string? slot) =>
            new(Path.Combine(PairnetsPaths.LocalDir, slot is null ? "token" : "token-" + slot), slot);

        public string Protect(string plainText) => Protect(plainText, null);

        public string Protect(string plainText, string? slot)
        {
            var (code, _) = Proc.Run("secret-tool", ["store", "--label=Pairnets sync token", .. Attributes(slot)], stdin: plainText);
            return code == 0 ? SecretSlots.Marker(Marker, slot) : Fallback(slot).Protect(plainText);
        }

        public string Unprotect(string protectedText)
        {
            if (SecretSlots.TryRead(PrivateFileSecrets.Marker, protectedText, out var fileSlot))
                return Fallback(fileSlot).Unprotect(protectedText);
            if (!SecretSlots.TryRead(Marker, protectedText, out var slot))
                throw new InvalidOperationException("Unknown token storage.");
            var (code, output) = Proc.Run("secret-tool", ["lookup", .. Attributes(slot)]);
            if (code == 0 && output.Length > 0)
                return output.Trim();
            if (slot is not null)
                throw new PlainMessageException("The token is not in the keyring (is it unlocked?).");
            // Saved by Tether: copy it to Pairnets' entry.
            (code, output) = Proc.Run("secret-tool", ["lookup", .. TetherAttributes]);
            if (code != 0 || output.Length == 0)
                throw new PlainMessageException("The token is not in the keyring (is it unlocked?).");
            Protect(output.Trim());
            return output.Trim();
        }

        public void Forget(string protectedText)
        {
            if (SecretSlots.TryRead(PrivateFileSecrets.Marker, protectedText, out var fileSlot))
            {
                Fallback(fileSlot).Forget(protectedText);
                return;
            }
            if (!SecretSlots.TryRead(Marker, protectedText, out var slot))
                return;
            // Pairnets' own entry only; one saved by Tether is left alone, as everywhere else.
            var (code, _) = Proc.Run("secret-tool", ["clear", .. Attributes(slot)]);
            if (code != 0)
                throw new PlainMessageException("The key could not be removed from the keyring (is it unlocked?).");
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
