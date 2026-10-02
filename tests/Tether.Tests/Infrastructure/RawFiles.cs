namespace Tether.Tests.Infrastructure;

/// <summary>
/// Creates files with names Win32 would normally rewrite (trailing dots or spaces), the way tools
/// using \\?\ paths or a Linux machine can. Used to prove such names are refused, not mangled.
/// </summary>
public static class RawFiles
{
    // No Path.GetFullPath here: on Windows it would strip the very trailing dot we want to keep.
    public static string ExactPath(string fullPath)
    {
        if (!Path.IsPathFullyQualified(fullPath))
            throw new ArgumentException("An absolute path is required.", nameof(fullPath));
        return OperatingSystem.IsWindows() ? @"\\?\" + fullPath : fullPath;
    }

    public static void Write(string fullPath, string content) => File.WriteAllText(ExactPath(fullPath), content);

    public static void Delete(string fullPath) => File.Delete(ExactPath(fullPath));
}
