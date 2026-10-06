namespace Tether.Core;

/// <summary>Product-wide constants shared by the server and the client.</summary>
public static class TetherInfo
{
    public const string ProductName = "Tether";

    /// <summary>Version of the HTTP API contract between client and server.</summary>
    public const int ApiVersion = 1;

    /// <summary>This build's release version, e.g. "1.0.58" (main builds are numbered 1.0.&lt;run&gt;).</summary>
    public static string ProductVersion { get; } = FormatVersion(typeof(TetherInfo).Assembly.GetName().Version);

    /// <summary>"Windows 1.0.60", "macOS 1.0.60" or "Linux 1.0.60": sent to the server so the other computers can show it.</summary>
    public static string AppDescription { get; } =
        (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + " " + ProductVersion;

    public static string FormatVersion(Version? v) =>
        v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
}
