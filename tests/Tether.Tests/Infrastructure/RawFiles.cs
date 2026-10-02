namespace Tether.Tests.Infrastructure;

/// <summary>
/// Creates files with names Win32 would normally rewrite (trailing dots or spaces), the way tools
/// using \\?\ paths or a Linux machine can. Used to prove such names are refused, not mangled.
/// </summary>
public static class RawFiles
{
    public static string ExactPath(string fullPath) =>
        OperatingSystem.IsWindows() ? @"\\?\" + Path.GetFullPath(fullPath) : fullPath;

    public static void Write(string fullPath, string content) => File.WriteAllText(ExactPath(fullPath), content);

    public static void Delete(string fullPath) => File.Delete(ExactPath(fullPath));
}
