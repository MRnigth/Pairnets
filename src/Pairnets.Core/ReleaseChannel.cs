using System.Globalization;

namespace Pairnets.Core;

/// <summary>
/// Where a Pairnets build comes from (docs/DECISIONS.md, "Beta updates"). Stable is the tagged releases everyone gets; a
/// computer whose account joined the beta program can choose Public beta or Developer beta in Settings. Each beta has
/// its own rolling GitHub pre-release; main's "Latest build" says "main" and is never installed by the apps.
/// </summary>
public static class ReleaseChannel
{
    public const string Stable = "stable";
    public const string PublicBeta = "public-beta";
    public const string DeveloperBeta = "developer-beta";

    /// <summary>Main's "Latest build" (numbered 1.0.&lt;run&gt;): signed, but no app ever installs it.</summary>
    public const string Main = "main";

    /// <summary>The choice "Off" in Settings, as the nest and the account page send it.</summary>
    public const string Off = "off";

    /// <summary>The choices offered in Settings, in order.</summary>
    public static readonly IReadOnlyList<string> Choices = [Stable, PublicBeta, DeveloperBeta];

    public static bool IsBeta(string? channel) => channel is PublicBeta or DeveloperBeta;

    /// <summary>"off", "stable", null or anything unknown is stable; the two betas are kept.</summary>
    public static string Normalize(string? channel) => IsBeta(channel) ? channel! : Stable;

    /// <summary>The word the nest and the account page use for <paramref name="channel"/> ("off" for stable).</summary>
    public static string WireName(string channel) => IsBeta(channel) ? channel : Off;

    /// <summary>"Public beta", "Developer beta", or "Off" for stable.</summary>
    public static string DisplayName(string? channel) => Normalize(channel) switch
    {
        PublicBeta => "Public beta",
        DeveloperBeta => "Developer beta",
        _ => "Off",
    };

    /// <summary>How far ahead a channel is: Off 0, Public beta 1, Developer beta 2 (moving down waits for a newer build).</summary>
    public static int Rank(string? channel) => Normalize(channel) switch
    {
        PublicBeta => 1,
        DeveloperBeta => 2,
        _ => 0,
    };

    /// <summary>The rolling GitHub pre-release a beta is published under (its tag), or null for stable.</summary>
    public static string? FeedTag(string channel) => IsBeta(channel) ? channel : null;
}

/// <summary>
/// One build: its version number, the channel it was built for and its build number (the release run; 0 for a build
/// that does not know it). Ordered by number, then a stable release before a beta of the same number (the stable
/// 1.0.61.8 is newer than every 1.0.61.8 beta), then the build number.
/// </summary>
public sealed record ReleaseVersion(VersionNumber Number, string Channel, int Build) : IComparable<ReleaseVersion>
{
    /// <summary>"1.0.61.8", "1.0.61.8 Public beta 3" or "1.0.61.8 Developer beta 12": the one spelling shown everywhere.</summary>
    public string Display => ReleaseChannel.IsBeta(Channel)
        ? $"{PairnetsInfo.FormatVersion(Number)} {ReleaseChannel.DisplayName(Channel)} {Build.ToString(CultureInfo.InvariantCulture)}"
        : PairnetsInfo.FormatVersion(Number);

    public bool IsBeta => ReleaseChannel.IsBeta(Channel);

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        var byNumber = Number.CompareTo(other.Number);
        if (byNumber != 0)
            return byNumber;
        if (IsBeta != other.IsBeta)
            return IsBeta ? -1 : 1;
        // Two stable builds of one number are the same release (a re-run of its workflow only gets a higher run number);
        // the build number only orders betas.
        return IsBeta ? Build.CompareTo(other.Build) : 0;
    }

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    /// <summary>"1.0.61", "v1.0.61.8", "1.0.61.8.1": 2 to 5 numbers (<see cref="VersionNumber"/>).</summary>
    public static bool TryParseNumber(string? text, out VersionNumber version) => VersionNumber.TryParse(text, out version);
}

/// <summary>
/// The newest data formats a build writes: settings.json (<c>SettingsUpgrades.Current</c>), the state database
/// (<c>StateDb.SchemaVersion</c>) and encrypted files (<c>SealedHeader.Version</c>). An older build refuses data written by
/// a newer one (StateDb stops with "created by a newer Pairnets"), so an update that would lower any of them is never
/// installed: a computer leaving a beta waits for a stable release that has everything the beta had.
/// </summary>
public sealed record DataVersions(int Settings, int State, int Sealed)
{
    /// <summary>This build's.</summary>
    public static DataVersions Current { get; } = new(Core.Settings.SettingsUpgrades.Current, Core.State.StateDb.SchemaVersion, Core.Crypto.SealedHeader.Version);

    /// <summary>What releases from before "data" was written into version.json (1.0.61.7 and older) had.</summary>
    public static DataVersions Before { get; } = new(2, 2, 1);

    /// <summary>True when a build with these data versions can read everything this build may have written.</summary>
    public bool CanRead(DataVersions written) => Settings >= written.Settings && State >= written.State && Sealed >= written.Sealed;
}
