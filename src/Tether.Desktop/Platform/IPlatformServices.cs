using Microsoft.Extensions.Logging;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Desktop.Platform;

/// <summary>Everything that differs between macOS and Linux desktops.</summary>
public interface IPlatformServices
{
    string Name { get; }

    /// <summary>Stores the token in the OS keychain/keyring (never in plain settings).</summary>
    ISecretProtector Secrets { get; }

    ILocalTrash CreateTrash(ILogger log);

    void Notify(string title, string text);

    bool IsAutoStartEnabled();

    void SetAutoStart(bool enabled);

    /// <summary>Opens a file or folder with the default app.</summary>
    void Open(string path);

    /// <summary>Shows a file selected in the file manager (or opens its folder).</summary>
    void Reveal(string path);

    public static IPlatformServices Current { get; } =
        OperatingSystem.IsMacOS() ? new MacPlatform() : new LinuxPlatform();
}

/// <summary>Runs helper programs without a shell (no quoting or injection issues).</summary>
internal static class Proc
{
    public static (int ExitCode, string StdOut) Run(string file, IEnumerable<string> args, string? stdin = null, int timeoutMs = 10_000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return (-1, string.Empty);
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                return (-1, string.Empty);
            }
            return (p.ExitCode, output.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, string.Empty);
        }
    }

    public static void Start(string file, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Opening a folder is a convenience; ignore failures.
        }
    }
}

/// <summary>
/// Fallback secret store: a file readable only by the current user (mode 600). Used on Linux
/// when no keyring (libsecret) is available.
/// </summary>
internal sealed class PrivateFileSecrets(string path) : ISecretProtector
{
    public const string Marker = "file:v1";

    public string Protect(string plainText)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Use DPAPI on Windows.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var w = new StreamWriter(fs))
            w.Write(plainText);
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        return Marker;
    }

    public string Unprotect(string protectedText)
    {
        if (protectedText != Marker)
            throw new InvalidOperationException("Unknown token storage.");
        return File.ReadAllText(path).Trim();
    }
}
