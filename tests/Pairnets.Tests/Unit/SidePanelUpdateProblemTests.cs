using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The side panel's "could not be installed" card (A13): one line says why, the button is the next step (or there is none),
/// and the X alone puts the card away.
/// </summary>
public class SidePanelUpdateProblemTests
{
    [Fact]
    public void AReleaseNotSignedByPairnetsSaysSoAndOffersNoButton()
    {
        var problem = UpdateProblem.From(new UpdateVerificationException(ReleaseSigning.NotSignedMessage, notSigned: true));

        Assert.Same(UpdateProblem.NotSigned, problem);
        Assert.Equal(ReleaseSigning.NotSignedMessage, problem.Reason);
        Assert.Equal(UpdateNextStep.None, problem.Next);
        Assert.Equal(string.Empty, problem.ButtonText);

        var card = SideUpdate.From("Pairnets 1.0.60 could not be installed", problem.Reason, problem.ButtonText);
        Assert.Equal(UpdateCardKind.Failed, card.Kind);
        Assert.True(card.ShowsReason);
        Assert.False(card.HasButton); // only the X
    }

    [Fact]
    public void EveryOtherFailureSaysWhyAndCanBeTriedAgain_NeverTheBrowser()
    {
        foreach (var error in new Exception[]
        {
            new UpdateVerificationException("checksum"), new HttpRequestException("reset"), new IOException("disk"),
            new TaskCanceledException(), new UnauthorizedAccessException(), new System.ComponentModel.Win32Exception(5),
        })
        {
            var problem = UpdateProblem.From(error, "/opt/Pairnets/Pairnets");
            Assert.Equal(UpdateNextStep.TryAgain, problem.Next);
            Assert.Equal(UpdateTexts.TryAgain, problem.ButtonText);
            Assert.Equal(UpdateTexts.Failed(error, "/opt/Pairnets/Pairnets"), problem.Reason); // the words 1.0.59.1 shipped with
        }
        Assert.Contains("checksum", UpdateProblem.From(new UpdateVerificationException("checksum")).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAFailedCardShowsItsReasonLine()
    {
        var ready = SideUpdate.From("Pairnets 1.0.60 is available", "You have 1.0.59.", "Update now");
        Assert.False(ready.ShowsReason);
        Assert.True(ready.HasButton);

        var downloading = SideUpdate.From("Downloading Pairnets 1.0.60…", "Syncing continues.", "Update now", 40, busy: true);
        Assert.False(downloading.ShowsReason);
        Assert.False(downloading.HasButton);

        var failed = SideUpdate.From("Pairnets 1.0.60 could not be installed", "The download broke off.", "Try again");
        Assert.True(failed.ShowsReason);
        Assert.True(failed.HasButton);
        Assert.Equal("The download broke off.", failed.Detail);
    }
}
