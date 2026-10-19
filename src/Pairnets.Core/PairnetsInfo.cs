namespace Pairnets.Core;

/// <summary>Product-wide constants shared by the server and the client.</summary>
public static class PairnetsInfo
{
    public const string ProductName = "Pairnets";

    /// <summary>Shown wherever the product names itself, e.g. the Settings version line.</summary>
    public const string Copyright = "© 2026 Pairnets";

    /// <summary>Version of the HTTP API contract between client and server.</summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// The Windows app's single-instance mutex. It must differ from the old app's (see the Legacy names): taking over
    /// an old install looks for that one to tell whether the old app is still running.
    /// </summary>
    public const string WindowsMutex = @"Local\Pairnets.Client.SingleInstance";

    /// <summary>The DPAPI entropy the Windows app saves its key with (the old app's is in the Legacy names).</summary>
    public const string DpapiEntropy = "Pairnets.Client.Token.v1";

    /// <summary>
    /// This build's release version, e.g. "1.0.58" (main builds are numbered 1.0.&lt;run&gt;), or "1.0.59.1" for a quick fix
    /// released from a v1.0.59.1 tag.
    /// </summary>
    public static string ProductVersion { get; } = FormatVersion(typeof(PairnetsInfo).Assembly.GetName().Version);

    /// <summary>"1.0.38; Windows": sent to the server so its Devices list shows each computer's app and system.</summary>
    public static string ClientDescription { get; } = $"{ProductVersion}; {SystemName()}";

    private static string SystemName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other";

    /// <summary>
    /// "1.0.58"; a fourth number only when there is one ("1.0.59.1"). It must not be dropped: a 1.0.59.1 that called itself
    /// 1.0.59 would offer the 1.0.59.1 release again and again, and its server would always look out of date.
    /// </summary>
    public static string FormatVersion(Version? v) =>
        v is null ? "0.0.0" : v.Revision > 0 ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{v.Revision}" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
}
