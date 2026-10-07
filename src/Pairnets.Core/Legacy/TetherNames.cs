namespace Pairnets.Core.Legacy;

/// <summary>
/// Names from when Pairnets was called Tether. Existing installs, synced folders, saved tokens and
/// apps that are not updated yet still use them, so the migration and compatibility code reads them
/// (and, on the wire, also sends them). Nothing else may use these.
/// </summary>
public static class TetherNames
{
    /// <summary>The old app folder under %AppData% and %LocalAppData% (and their Mac/Linux equivalents).</summary>
    public const string AppFolder = "Tether";

    public const string MarkerFileName = ".tether-marker";
    public const string TempFolderName = ".tether-tmp";
    public const string LogFilePattern = "tether-*.log";

    public const string ServerIdHeader = "X-Tether-Server-Id";
    public const string VersionHeader = "X-Tether-Version";
    public const string HashHeader = "X-Tether-Hash";
    public const string ModifiedMsHeader = "X-Tether-Modified-Ms";
    public const string ClientHeader = "X-Tether-Client";

    /// <summary>The DPAPI entropy the Windows app used for the token.</summary>
    public const string DpapiEntropy = "Pairnets.Client.Token.v1";

    /// <summary>The Windows app's single-instance mutex, to tell whether the old app is still running.</summary>
    public const string WindowsMutex = @"Local\Pairnets.Client.SingleInstance";

    /// <summary>The Mac/Linux app's single-instance lock file, inside the old local app folder.</summary>
    public const string DesktopLockFile = "app.lock";

    public const string KeychainService = "Tether";
    public const string SecretToolService = "tether";

    public const string WindowsRunValue = "Tether";
    public const string MacLaunchAgentFile = "app.tether.client.plist";
    public const string LinuxAutostartFile = "tether.desktop";
}
