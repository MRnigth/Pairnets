using Pairnets.Core.Api;

namespace Pairnets.Core.Client;

/// <summary>What the "could not be installed" card's button does next.</summary>
public enum UpdateNextStep
{
    /// <summary>No button: there is nothing to do (a release Pairnets did not sign is never offered). The X puts the card away.</summary>
    None,

    /// <summary>
    /// "Try again": every other failure. Nothing was changed, and the app never sends the person to the browser instead
    /// (the card says why it failed, see <see cref="UpdateTexts.Failed"/>).
    /// </summary>
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
    public string ButtonText => Next == UpdateNextStep.TryAgain ? UpdateTexts.TryAgain : string.Empty;

    /// <summary>The problem behind an update that failed with <paramref name="error"/>; <paramref name="appPath"/> is the app's own file.</summary>
    public static UpdateProblem From(Exception error, string? appPath = null) => error switch
    {
        UpdateVerificationException { NotSigned: true } => NotSigned,
        _ => new(UpdateTexts.Failed(error, appPath), UpdateNextStep.TryAgain),
    };
}
