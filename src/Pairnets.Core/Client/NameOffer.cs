using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// The one-time card on the Needs attention page that offers this computer's own name ("StudyPC") instead of a
/// placeholder it got earlier ("PC-1", or the all-capitals "STUDYPC"). Either answer is final: it is not offered again.
/// </summary>
public sealed record NameOffer(string Current, string Own)
{
    /// <summary>The offer for this computer, or null when its name is fine, it has no own key or the person already answered.</summary>
    public static NameOffer? For(ClientSettings settings, string own) =>
        settings.HasOwnKey && !settings.NameOfferAnswered && ComputerName.WorthOffering(settings.DeviceName, own)
            ? new NameOffer(settings.DeviceName!, own)
            : null;

    public string Title => "Use this computer's own name?";

    public string Detail => $"In your nest this computer is called {Current}. Its own name is {Own}. You can change it any time in Settings or on your account page at pairnets.app.";

    public string UseLabel => "Use " + Own;

    public string KeepLabel => "Keep " + Current;
}
