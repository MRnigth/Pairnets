namespace Pairnets.Core;

/// <summary>
/// The Developer beta look (docs/DECISIONS.md, "Developer beta look"): a redesign that no channel shows by default since
/// 1.0.61.14.7 (every build draws the 1.0.61.14 stable look). <c>PAIRNETS_LOOK=devbeta</c> turns it on and
/// <c>PAIRNETS_LOOK=off</c> turns it off in any build (local screenshots and tests).
/// </summary>
public static class DevBetaLook
{
    public const string Variable = "PAIRNETS_LOOK";

    /// <summary>True when this process draws the Developer beta look.</summary>
    public static bool On { get; } = For(PairnetsInfo.Channel, Environment.GetEnvironmentVariable(Variable));

    /// <summary>The rule on its own, for tests: only the override turns it on; every channel draws the stable look.</summary>
    public static bool For(string? channel, string? overrideValue) => overrideValue?.Trim().ToLowerInvariant() switch
    {
        "devbeta" => true,
        _ => false,
    };
}
