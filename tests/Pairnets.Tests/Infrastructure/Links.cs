using System.Diagnostics;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// Links for the "never follow a link" tests. Windows only lets an elevated user (or Developer Mode) make symbolic
/// links; without that, a folder link becomes a junction, which is the same kind of reparse point for the scanner
/// and needs no rights.
/// </summary>
public static class Links
{
    public static void Folder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException)
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode != 0 || !Directory.Exists(link))
                throw new IOException($"Could not make a junction {link} -> {target}: {mklink.StandardError.ReadToEnd()}", e);
        }
    }

    /// <summary>A file link, or false where Windows does not allow one (there is no junction for files).</summary>
    public static bool TryFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
