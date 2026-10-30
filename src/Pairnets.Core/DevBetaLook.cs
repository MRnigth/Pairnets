namespace Pairnets.Core;

/// <summary>
/// The Developer beta look (docs/DECISIONS.md, "Developer beta look"): a redesign that both beta channels show,
/// so stable never changes. <c>PAIRNETS_LOOK=devbeta</c> turns it on and <c>PAIRNETS_LOOK=off</c> turns it
/// off in any build (local screenshots and tests); without it the build's own channel decides.
/// </summary>
public static class DevBetaLook
{
    public const string Variable = "PAIRNETS_LOOK";

    /// <summary>True when this process draws the Developer beta look.</summary>
    public static bool On { get; } = For(PairnetsInfo.Channel, Environment.GetEnvironmentVariable(Variable));

    /// <summary>The rule on its own, for tests: the override wins, otherwise both beta channels.</summary>
    public static bool For(string? channel, string? overrideValue) => overrideValue?.Trim().ToLowerInvariant() switch
    {
        "devbeta" => true,
        "off" => false,
        _ => ReleaseChannel.Normalize(channel) is ReleaseChannel.PublicBeta or ReleaseChannel.DeveloperBeta,
    };
}
