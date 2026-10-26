using System.Globalization;
using System.Reflection;

namespace Pairnets.Core;

/// <summary>Product-wide constants shared by the server and the client.</summary>
public static class PairnetsInfo
{
    public const string ProductName = "Pairnets";

    /// <summary>Shown wherever the product names itself, e.g. the Settings version line.</summary>
    public const string Copyright = "© 2026 MRnigth";

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
    /// This build's release version, e.g. "1.0.58" (main builds are numbered 1.0.&lt;run&gt;), "1.0.59.1" for a quick fix
    /// released from a v1.0.59.1 tag, or "1.0.61.8.1" from a five-number tag (the release workflow builds with the full
    /// number as InformationalVersion and its first four as Version, the most dotnet accepts).
    /// </summary>
    public static string ProductVersion { get; } = ReadVersion(typeof(PairnetsInfo).Assembly);

    /// <summary>The informational version without any "+commit" ending when it is a release number, else the assembly version.</summary>
    public static string ReadVersion(System.Reflection.Assembly assembly)
    {
        var info = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return FromInformational(info) ?? FormatVersion(assembly.GetName().Version);
    }

    /// <summary>"1.0.61.8.1+4f2c9e1" → "1.0.61.8.1"; null when it is not a release number ("1.0.0-dev", empty).</summary>
    public static string? FromInformational(string? informational)
    {
        var text = informational?.Split('+', 2)[0].Trim();
        // "1.0.58.0" from a 4-number Version reads as 1.0.58, as it always has.
        return VersionNumber.TryParse(text, out var version) ? FormatVersion(version) : null;
    }

    /// <summary>The channel this build was made for (<see cref="ReleaseChannel"/>); a local build says stable.</summary>
    public static string Channel { get; } = ReleaseChannel.Normalize(Metadata("PairnetsChannel"));

    /// <summary>The release run this build came from; 0 for a local build.</summary>
    public static int Build { get; } = int.TryParse(Metadata("PairnetsBuild"), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;

    /// <summary>This build, for comparing with releases: <see cref="ProductVersion"/>, all five numbers of a five-number release.</summary>
    public static ReleaseVersion ThisRelease { get; } =
        new(VersionNumber.TryParse(ProductVersion, out var n) ? n : VersionNumber.Zero, Channel, Build);

    /// <summary>"1.0.61.8", or "1.0.61.8 Public beta 3" on a beta: the version as the apps and the account page show it.</summary>
    public static string DisplayVersion => ThisRelease.Display;

    /// <summary>
    /// The stable release a beta build follows ("1.0.61.7" for a 1.0.61.8 beta, which is numbered as the next stable), or
    /// <see cref="ProductVersion"/> on a stable build. A nest only runs stable releases, so a beta app compares it with this.
    /// </summary>
    public static string StableBaseVersion => ThisRelease.IsBeta ? FormatVersion(PreviousNumber(ThisRelease.Number)) : ProductVersion;

    /// <summary>"1.0.38; Windows" ("1.0.61.8 Public beta 3; Windows" on a beta): sent to the server so its Devices list shows each computer's app and system.</summary>
    public static string ClientDescription { get; } = $"{DisplayVersion}; {SystemName()}";

    private static string? Metadata(string key) =>
        typeof(PairnetsInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>1.0.61.8 → 1.0.61.7, 1.0.61.1 → 1.0.61, 1.0.62 → 1.0.61, 1.0.61.8.1 → 1.0.61.8.</summary>
    internal static VersionNumber PreviousNumber(VersionNumber v) => v.Previous();

    private static string SystemName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other";

    /// <summary>
    /// "1.0.58"; a fourth number only when there is one ("1.0.59.1"). It must not be dropped: a 1.0.59.1 that called itself
    /// 1.0.59 would offer the 1.0.59.1 release again and again, and its server would always look out of date.
    /// </summary>
    public static string FormatVersion(Version? v) => FormatVersion(v is null ? null : (VersionNumber)v);

    /// <summary>As <see cref="FormatVersion(Version?)"/>, with a fifth number when there is one ("1.0.61.8.1").</summary>
    public static string FormatVersion(VersionNumber? v) => v is null ? "0.0.0" : string.Join('.', v.Significant);
}
