using Pairnets.Core.Api;

namespace Pairnets.Core.Client;

/// <summary>What the "could not be installed" card's button does next.</summary>
public enum UpdateNextStep
{
    /// <summary>No button: there is nothing to do (a release Pairnets did not sign is never offered). The X puts the card away.</summary>
    None,

    /// <summary>"Open download page": the release page, to install it by hand.</summary>
    OpenDownloadPage,

    /// <summary>"Try again": the download broke off (the network, a full disk), so another try may work.</summary>
    TryAgain,
}

/// <summary>
/// Why an app update could not be installed, in one line the card shows, and the next step its button offers. Both apps
/// build the card from this, so they say the same.
/// </summary>
public sealed record UpdateProblem(string Reason, UpdateNextStep Next)
{
    /// <summary>The release is not signed by Pairnets: it is never installed or pointed to.</summary>
    public static UpdateProblem NotSigned { get; } = new(ReleaseSigning.NotSignedMessage, UpdateNextStep.None);

    /// <summary>The words on the card's button; empty when there is none.</summary>
    public string ButtonText => Next switch
    {
        UpdateNextStep.OpenDownloadPage => "Open download page",
        UpdateNextStep.TryAgain => "Try again",
        _ => string.Empty,
    };

    /// <summary>The problem behind an update that failed with <paramref name="error"/>.</summary>
    public static UpdateProblem From(Exception error) => error switch
    {
        UpdateVerificationException { NotSigned: true } => NotSigned,
        UpdateVerificationException => new("The download didn't match its checksum, so Pairnets kept your current version.", UpdateNextStep.OpenDownloadPage),
        HttpRequestException or TaskCanceledException => new("The download broke off. Check that this computer is online, then try again.", UpdateNextStep.TryAgain),
        IOException => new("The download failed. " + FriendlyErrors.Describe(error), UpdateNextStep.TryAgain),
        _ => new("The installer could not start. " + FriendlyErrors.Describe(error), UpdateNextStep.OpenDownloadPage),
    };
}
