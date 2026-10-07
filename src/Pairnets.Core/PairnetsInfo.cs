namespace Pairnets.Core;

/// <summary>Product-wide constants shared by the server and the client.</summary>
public static class PairnetsInfo
{
    public const string ProductName = "Pairnets";

    /// <summary>Version of the HTTP API contract between client and server.</summary>
    public const int ApiVersion = 1;

    /// <summary>This build's release version, e.g. "1.0.58" (main builds are numbered 1.0.&lt;run&gt;).</summary>
    public static string ProductVersion { get; } = FormatVersion(typeof(PairnetsInfo).Assembly.GetName().Version);

    /// <summary>"1.0.38; Windows": sent to the server so its Devices list shows each computer's app and system.</summary>
    public static string ClientDescription { get; } = $"{ProductVersion}; {SystemName()}";

    private static string SystemName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other";

    public static string FormatVersion(Version? v) =>
        v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
}
