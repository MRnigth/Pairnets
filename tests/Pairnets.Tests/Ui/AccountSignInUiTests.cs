using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The sign-in window's account steps in the Mac/Linux app (the Windows app is the same, page for page), driven by
/// pressing their buttons against a stand-in Pairnets service: sign in → finish in the browser → no nest yet → the folder.
/// </summary>
public class AccountSignInUiTests
{
    /// <summary>Keeps the key readable, so the test can see what would be saved.</summary>
    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plainText) => "protected:" + plainText;

        public string Unprotect(string protectedText) => protectedText["protected:".Length..];
    }

    private sealed class Harness : IDisposable
    {
        public Harness(FakeSyncService service, ClientSettings? settings = null)
        {
            Host = new SettingsWindow(settings ?? new ClientSettings { DeviceName = "LAPTOP" }, new PlainSecrets(), autoStart: false, Opened.Enqueue);
            View.Service = service.Url;
            View.AccountPollInterval = TimeSpan.FromMilliseconds(50);
            Host.Show();
        }

        public SettingsWindow Host { get; }

        public SignInView View => Host.SignIn;

        /// <summary>The pages the window opened in the browser, in order.</summary>
        public ConcurrentQueue<string> Opened { get; } = new();

        public void Dispose() => Host.Close();
    }

    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyVisible, $"{button.Name} is not on show");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets the window take in what the sign-in (on another thread) reports, until <paramref name="done"/>.</summary>
    private static void WaitFor(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (done())
                return;
            if (DateTime.UtcNow > until)
                Assert.Fail("Timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    [AvaloniaFact]
    public async Task ContinueWithEmailGoesThroughTheBrowserAndNoNestYetToTheFolder()
    {
        await using var nest = await TestServer.StartAsync();
        await using var service = await FakeSyncService.StartAsync(nest);
        var origin = service.Url.GetLeftPart(UriPartial.Authority);
        using var folder = new TempDir("account-ui");
        using var window = new Harness(service);
        var view = window.View;
        Assert.Equal(SignInStep.Account, view.Step);

        // Allowed in the browser while the account has no nest: the window says so and keeps waiting.
        service.PollScript.Enqueue("pending");
        service.PollScript.Enqueue("nest_offline");
        service.PollScript.Enqueue("nest_offline");
        service.AfterScript = "no_nest";
        view.AccountEmailBox.Text = "you@example.com";
        Click(view.AccountEmailButton);
        Assert.Equal(SignInStep.AccountWait, view.Step);
        WaitFor(() => !window.Opened.IsEmpty, "the browser page");
        Assert.Equal($"{origin}/app?code={FakeSyncService.UserCode}&method=email&email=you%40example.com", Assert.Single(window.Opened));
        Assert.Equal(FakeSyncService.UserCode, view.AccountCodeText.Text);
        Assert.Equal("127.0.0.1", view.AccountServiceRun.Text);
        WaitFor(() => view.AccountWaitText.Text == AccountSignIn.ServerOfflineMessage, "the server-offline note");

        WaitFor(() => view.Step == SignInStep.NoNest, "the no-nest step");
        Assert.Equal("Signed in as y•••@example.com", view.NoNestAccountText.Text);
        Assert.Equal("Waiting for your nest…", view.NoNestWaitText.Text);
        Assert.True(view.NoNestFooter.IsVisible);
        Click(view.ShowMeHowButton);
        Assert.Equal($"{origin}/account", window.Opened.Last());

        // The account adds its nest: this computer joins it by itself, and goes on to the folder.
        service.AfterScript = "approved";
        WaitFor(() => view.Step == SignInStep.Folder, "the folder step");
        Assert.Equal("✓ Signed in as LAPTOP on soro", view.SignedInText.Text);
        Assert.False(view.Footer.IsVisible);
        Assert.Equal(2, window.Opened.Count); // the page was opened once, not again for every answer

        view.Folder = folder.Path;
        Click(view.StartButton);
        WaitFor(() => window.Host.Result is not null, "the settings to save");
        var saved = window.Host.Result!;
        Assert.Equal(service.RelayUrl.ToString(), saved.ServerUrl);
        Assert.Equal(FakeSyncService.Email, saved.AccountEmail);
        Assert.Equal("LAPTOP", saved.DeviceName);
        Assert.Equal(folder.Path, saved.Folder);
        Assert.Equal("protected:" + window.Host.PlainToken, saved.ProtectedToken);
        Assert.True(saved.FirstRunCompleted);
        Assert.True(saved.HasOwnKey);
    }

    [AvaloniaFact]
    public async Task CancelAndSignOutGoBackToTheFirstStep()
    {
        await using var service = await FakeSyncService.StartAsync();
        var origin = service.Url.GetLeftPart(UriPartial.Authority);
        using var window = new Harness(service);
        var view = window.View;

        // No address yet: the email button says so and stays on the first step.
        Click(view.AccountEmailButton);
        Assert.True(view.AccountEmailProblem.IsVisible);
        Assert.Equal(SignInStep.Account, view.Step);
        Assert.Empty(window.Opened);

        // Google, then Cancel while waiting for the browser.
        service.AfterScript = "pending";
        Click(view.AccountGoogleButton);
        WaitFor(() => !window.Opened.IsEmpty, "the browser page");
        Assert.Equal($"{origin}/app?code={FakeSyncService.UserCode}&method=google", window.Opened.Last());
        Assert.True(view.AccountWaitFooter.IsVisible);
        WaitFor(() => view.AccountExpiresText.Text?.StartsWith("Code expires in 9:", StringComparison.Ordinal) == true, "the countdown");
        Click(view.AccountOpenAgainButton);
        Assert.Equal(2, window.Opened.Count);
        Click(view.AccountCancelButton);
        Assert.Equal(SignInStep.Account, view.Step);
        Assert.False(view.AccountOutcomeBox.IsVisible);
        var polls = service.Polls;
        Thread.Sleep(300);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(polls, service.Polls); // the sign-in stopped asking
        Assert.Equal(SignInStep.Account, view.Step);

        // Email with no nest yet, then Sign out.
        service.AfterScript = "no_nest";
        view.AccountEmailBox.Text = "you@example.com";
        Dispatcher.UIThread.RunJobs(); // TextChanged arrives through the dispatcher
        Assert.False(view.AccountEmailProblem.IsVisible);
        Click(view.AccountEmailButton);
        WaitFor(() => view.Step == SignInStep.NoNest, "the no-nest step");
        Click(view.NoNestSignOutButton);
        Assert.Equal(SignInStep.Account, view.Step);
        Assert.True(view.AccountFooter.IsVisible);

        // "Make an account": accounts are made by signing in.
        Click(view.MakeAccountButton);
        Assert.Equal($"{origin}/login", window.Opened.Last());
    }

    [AvaloniaFact]
    public async Task TurnedDownTooLateOrUnreachableGoesBackToTheFirstStepWithTheReason()
    {
        await using var service = await FakeSyncService.StartAsync();
        using var window = new Harness(service);
        var view = window.View;

        service.AfterScript = "denied";
        Click(view.AccountGoogleButton);
        WaitFor(() => view.Step == SignInStep.Account, "the first step");
        Assert.True(view.AccountOutcomeBox.IsVisible);
        Assert.Contains("turned down in the browser", view.AccountOutcomeText.Text);

        service.AfterScript = "expired";
        Click(view.AccountGoogleButton);
        WaitFor(() => view.Step == SignInStep.AccountWait, "the browser step");
        WaitFor(() => view.Step == SignInStep.Account, "the first step");
        Assert.StartsWith("The code expired", view.AccountOutcomeText.Text);

        view.Service = new Uri("http://127.0.0.1:9/");
        Click(view.AccountGoogleButton);
        WaitFor(() => view.Step == SignInStep.Account && view.AccountOutcomeText.Text?.StartsWith("Can't reach Pairnets", StringComparison.Ordinal) == true,
            "the unreachable note");

        // Trying again clears the note.
        view.Service = service.Url;
        service.AfterScript = "pending";
        Click(view.AccountGoogleButton);
        Click(view.AccountCancelButton);
        Assert.False(view.AccountOutcomeBox.IsVisible);
    }

    [AvaloniaFact]
    public async Task IRunMyOwnNestLeadsToTheNestsAddressStep()
    {
        await using var service = await FakeSyncService.StartAsync();
        using var window = new Harness(service, new ClientSettings { DeviceName = "LAPTOP", ServerUrl = "https://nest.example.invalid/" });
        var view = window.View;
        Assert.Equal(SignInStep.Account, view.Step); // the account first, also for a computer that used its own nest

        Click(view.OwnNestButton);
        Assert.Equal(SignInStep.Address, view.Step);
        Assert.True(view.WelcomeStep.IsVisible);
        Assert.Equal("nest.example.invalid", view.AddressBox.Text); // its nest, as before
        Click(view.BackToAccountButton);
        Assert.Equal(SignInStep.Account, view.Step);

        // From "no nest yet" too: the account sign-in stops.
        service.AfterScript = "no_nest";
        view.AccountEmailBox.Text = "you@example.com";
        Click(view.AccountEmailButton);
        WaitFor(() => view.Step == SignInStep.NoNest, "the no-nest step");
        Click(view.NoNestOwnNestButton);
        Assert.Equal(SignInStep.Address, view.Step);
        var polls = service.Polls;
        Thread.Sleep(300);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(polls, service.Polls);
        Assert.Equal(SignInStep.Address, view.Step);

        // A computer that signed in with the account has no nest name of its own to suggest.
        using var relayed = new Harness(service, new ClientSettings { ServerUrl = Relay.AddressOf(FakeSyncService.NestId).ToString() });
        Assert.Equal(string.Empty, relayed.View.AddressBox.Text);
    }
}
