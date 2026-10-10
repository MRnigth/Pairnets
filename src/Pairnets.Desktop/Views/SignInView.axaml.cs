using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>The steps of the sign-in window, one shown at a time.</summary>
internal enum SignInStep
{
    /// <summary>"Sign in to Pairnets": Continue with Google or email.</summary>
    Account,

    /// <summary>"Finish in your browser": the code, until the computer is allowed.</summary>
    AccountWait,

    /// <summary>Allowed, but the account has no nest yet.</summary>
    NoNest,

    /// <summary>"I run my own nest": its address and the ways to sign in there.</summary>
    Address,

    /// <summary>"Approve this computer" on the nest.</summary>
    Wait,

    /// <summary>Signed in: choose the folder.</summary>
    Folder,
}

/// <summary>
/// Setting up a computer by signing in: with a Pairnets account (Google or email) in the browser, or on your own nest
/// from its address, then pick the folder. The steps and their network work live in <see cref="AccountSignIn"/>,
/// <see cref="PairingFlow"/> and <see cref="Nest"/>; this view only draws them.
/// </summary>
public partial class SignInView : UserControl
{
    private readonly ClientSettings _current;
    private readonly ISecretProtector? _protector;
    private readonly Action<string> _openUrl;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _checking;
    private CancellationTokenSource? _flowStop;
    private CancellationTokenSource? _accountStop;
    private NestCheck? _check;
    private PairingState? _state;
    private AccountSignInState? _account;
    private AccountSignInResult? _accountResult;
    private Uri? _nest;
    private string? _method;
    private string? _email;
    private bool _browserOpened;
    private bool _accountBrowserOpened;
    private bool _quiet;
    private string? _shownAddress;

    public SignInView()
        : this(new ClientSettings(), null, autoStart: false, _ => { })
    {
    }

    public SignInView(ClientSettings current, ISecretProtector? protector, bool autoStart, Action<string> openUrl, string? nestHint = null)
    {
        InitializeComponent();
        _current = current;
        _protector = protector;
        _openUrl = openUrl;
        FolderBox.Text = current.Folder ?? string.Empty;
        AutoStartBox.IsChecked = autoStart || current.StartWithWindows;
        AddressBox.Text = SuggestedAddress(current, nestHint);
        AddressBox.TextChanged += (_, _) =>
        {
            // Avalonia may raise this after the text was set (so after _quiet is off again): an address that was
            // just shown with its result must not start a second look-up that blanks the result.
            if (!_quiet && AddressBox.Text != _shownAddress)
                CheckSoon();
        };
        EmailBox.TextChanged += (_, _) =>
            EmailButton.IsEnabled = _check is { CanSignIn: true } && Nest.LooksLikeEmail(EmailBox.Text);
        AccountEmailBox.TextChanged += (_, _) => AccountEmailProblem.IsVisible = false;
        AccountEmailBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            ContinueWithEmail();
        };
        _clock.Tick += (_, _) => DrawWaiting();
        DetachedFromVisualTree += (_, _) =>
        {
            _clock.Stop();
            _flowStop?.Cancel();
            _accountStop?.Cancel();
            _checking?.Cancel();
        };
        ShowStep(SignInStep.Account);
        if (!string.IsNullOrWhiteSpace(AddressBox.Text))
            CheckSoon(TimeSpan.Zero);
    }

    /// <summary>Signed in and a folder chosen: the settings to save (key protected) and the plain key for this session.</summary>
    public event Action<ClientSettings, string>? SignedIn;

    /// <summary>The Pairnets service to sign in with (tests use a stand-in); by default sync.pairnets.app.</summary>
    internal Uri? Service { get; set; }

    /// <summary>How often the account sign-in asks how it went (tests); by default what the service says.</summary>
    internal TimeSpan? AccountPollInterval { get; set; }

    /// <summary>The step on show.</summary>
    internal SignInStep Step { get; private set; }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>
    /// The nest this computer used before (signing in again), as people type it. An old install may still
    /// point at a plain address; the nest's own name, learned while syncing, then fills the field instead.
    /// A relay address is not a nest's own name: signing in again goes through the account.
    /// </summary>
    private static string SuggestedAddress(ClientSettings current, string? nestHint) =>
        Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && !Relay.IsRelayAddress(url)
            ? url.IsDefaultPort ? url.Host : url.Authority
            : Uri.TryCreate(nestHint, UriKind.Absolute, out var nest) && nest.Scheme == Uri.UriSchemeHttps
                ? nest.IsDefaultPort ? nest.Host : nest.Authority
                : string.Empty;

    /// <summary>Shows one step (and its bottom line); the spinners turn only while theirs is on show.</summary>
    private void ShowStep(SignInStep step)
    {
        Step = step;
        AccountStep.IsVisible = step == SignInStep.Account;
        AccountWaitStep.IsVisible = step == SignInStep.AccountWait;
        NoNestStep.IsVisible = step == SignInStep.NoNest;
        WelcomeStep.IsVisible = step == SignInStep.Address;
        WaitStep.IsVisible = step == SignInStep.Wait;
        FolderStep.IsVisible = step == SignInStep.Folder;
        AccountFooter.IsVisible = AccountStep.IsVisible;
        AccountWaitFooter.IsVisible = AccountWaitStep.IsVisible;
        NoNestFooter.IsVisible = NoNestStep.IsVisible;
        AddressFooter.IsVisible = WelcomeStep.IsVisible;
        Footer.IsVisible = step is SignInStep.Account or SignInStep.AccountWait or SignInStep.NoNest or SignInStep.Address;
        AccountRing.Classes.Set("spin", step == SignInStep.AccountWait);
        NoNestRing.Classes.Set("spin", step == SignInStep.NoNest);
    }

    private string ThisName() => _current.DeviceName ?? Environment.MachineName;

    // ------------------------------------------------------------------ 1. sign in to Pairnets

    private void OnAccountGoogle(object? sender, RoutedEventArgs e) => StartAccount("google", null);

    private void OnAccountEmail(object? sender, RoutedEventArgs e) => ContinueWithEmail();

    private void ContinueWithEmail()
    {
        if (!Nest.LooksLikeEmail(AccountEmailBox.Text))
        {
            AccountEmailProblem.IsVisible = true;
            AccountEmailBox.Focus();
            return;
        }
        StartAccount("email", (AccountEmailBox.Text ?? string.Empty).Trim());
    }

    /// <summary>Accounts are made by signing in: "Make an account" opens the service's sign-in page.</summary>
    private void OnMakeAccount(object? sender, RoutedEventArgs e) => _openUrl(Relay.LoginUrl(Service));

    /// <summary>The small print under the sign-in buttons: the website's Terms and Privacy Policy.</summary>
    private void OnTerms(object? sender, RoutedEventArgs e) => _openUrl(PairnetsLinks.Terms);

    private void OnPrivacy(object? sender, RoutedEventArgs e) => _openUrl(PairnetsLinks.Privacy);

    private void OnOwnNest(object? sender, RoutedEventArgs e) => ShowAddressStep();

    private void OnBackToAccount(object? sender, RoutedEventArgs e) => ShowAccountStep(null);

    /// <summary>The first step, with why the last sign-in did not finish (if it did not).</summary>
    internal void ShowAccountStep(string? message)
    {
        _accountStop?.Cancel();
        _flowStop?.Cancel();
        _clock.Stop();
        AccountOutcomeBox.IsVisible = message is not null;
        AccountOutcomeText.Text = message ?? string.Empty;
        ShowStep(SignInStep.Account);
    }

    /// <summary>"I run my own nest": the nest's own sign-in, from its address. An account sign-in under way stops.</summary>
    internal void ShowAddressStep()
    {
        _accountStop?.Cancel();
        _clock.Stop();
        ShowStep(SignInStep.Address);
    }

    private void StartAccount(string method, string? email)
    {
        _accountStop?.Cancel();
        var stop = _accountStop = new CancellationTokenSource();
        _accountBrowserOpened = false;
        var flow = new AccountSignIn(ThisName(), method, Service, interval: AccountPollInterval, email: email);
        flow.Changed += s => Dispatcher.UIThread.Post(() =>
        {
            if (!stop.IsCancellationRequested)
                ShowAccount(s);
        });
        ShowAccount(new AccountSignInState(PairingStage.Starting));
        _ = Task.Run(() => flow.RunAsync(stop.Token));
    }

    // ------------------------------------------------------------------ 2. finish in the browser (and: no nest yet)

    /// <summary>Draws an account sign-in: the browser step, "no nest yet", the folder once allowed, or back to the start.</summary>
    internal void ShowAccount(AccountSignInState state, bool openBrowser = true)
    {
        _account = state;
        if (state.Stage == PairingStage.Approved && state.Result is { } result)
        {
            _clock.Stop();
            ShowFolderStep(result);
            return;
        }
        if (state.IsFinished)
        {
            ShowAccountStep(AccountOutcome(state));
            return;
        }
        if (state.Stage == PairingStage.Waiting && openBrowser && !_accountBrowserOpened && state.VerifyUrl is { } link)
        {
            _accountBrowserOpened = true;
            _openUrl(link);
        }
        if (state.NoNest && !state.ServerOffline)
        {
            _clock.Stop();
            NoNestAccountText.Text = AccountSignIn.MaskEmail(state.AccountEmail) is { } masked ? $"Signed in as {masked}" : "Signed in";
            NoNestWaitText.Text = state.Message is { } trouble && trouble != AccountSignIn.NoNestMessage ? trouble : "Waiting for your nest…";
            ShowStep(SignInStep.NoNest);
            return;
        }
        var starting = state.Stage == PairingStage.Starting;
        AccountWaitIntro.IsVisible = !starting;
        AccountStartingText.IsVisible = starting;
        AccountServiceRun.Text = Uri.TryCreate(state.VerifyUrl, UriKind.Absolute, out var page) ? page.Host : (Service ?? Relay.DefaultServiceUrl).Host;
        AccountCodeText.Text = state.Code ?? "····-····";
        AccountWaitText.Text = starting ? "Asking Pairnets for a code…" : state.Message ?? "Waiting for you to allow this computer…";
        AccountOpenAgainButton.IsEnabled = state.VerifyUrl is not null;
        ShowStep(SignInStep.AccountWait);
        _clock.Start();
        DrawWaiting();
    }

    /// <summary>Why an account sign-in ended without this computer getting in, for the first step.</summary>
    private static string AccountOutcome(AccountSignInState state) => state.Stage switch
    {
        PairingStage.Denied => "This computer was turned down in the browser. If that was a mistake, try again and press Allow.",
        PairingStage.Expired when state.NoNest => "Pairnets stopped waiting for your nest after 30 minutes. Sign in again once your nest is set up.",
        PairingStage.Expired => "The code expired before this computer was allowed (codes last 10 minutes). Try again.",
        _ => state.Message ?? "Signing in did not finish. Try again.",
    };

    private void OnAccountOpenAgain(object? sender, RoutedEventArgs e)
    {
        if (_account?.VerifyUrl is { } link)
            _openUrl(link);
    }

    /// <summary>"Cancel" while waiting, "Sign out" while there is no nest yet: stop, and back to the first step.</summary>
    private void OnAccountCancel(object? sender, RoutedEventArgs e) => ShowAccountStep(null);

    /// <summary>"Show me how": the account page says how to set up a nest.</summary>
    private void OnShowMeHow(object? sender, RoutedEventArgs e) => _openUrl(Relay.AccountUrl(Service));

    // ------------------------------------------------------------------ 4. which nest

    private void CheckSoon(TimeSpan? delay = null)
    {
        _checking?.Cancel();
        var cts = _checking = new CancellationTokenSource();
        _check = null;
        DrawMethods();
        var text = AddressBox.Text;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromMilliseconds(600), cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested && !string.IsNullOrWhiteSpace(text))
                        ShowCheck(new NestCheck(NestCheckStatus.Empty, null, null, "Looking for your nest…"));
                });
                var check = await Nest.CheckAsync(text, ct: cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested)
                        ShowCheck(check);
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Shows an address and what was found there, without looking it up (screenshot test).</summary>
    internal void ShowAddress(string text, NestCheck check)
    {
        _checking?.Cancel();
        _quiet = true;
        _shownAddress = text;
        AddressBox.Text = text;
        _quiet = false;
        ShowCheck(check);
        ShowAddressStep();
    }

    /// <summary>Draws the line under "Your nest" (also used by the screenshot test).</summary>
    internal void ShowCheck(NestCheck check)
    {
        _check = check;
        var empty = check.Status == NestCheckStatus.Empty;
        CheckRow.IsVisible = check.Message.Length > 0;
        CheckText.Text = check.Message;
        var (icon, brush) = check.Status switch
        {
            NestCheckStatus.Found => ("I.Check", Visuals.Resource<IBrush>("S.Green")),
            NestCheckStatus.Unreachable or NestCheckStatus.Invalid or NestCheckStatus.NotPairnets => ("I.X", Visuals.Resource<IBrush>("S.Red")),
            NestCheckStatus.NoSignIn => ("I.Info", Visuals.Resource<IBrush>("S.Orange")),
            _ => ("I.Wait", Visuals.Resource<IBrush>("T.Muted")),
        };
        CheckIcon.Data = Visuals.Resource<Geometry>(icon);
        CheckIcon.Stroke = brush;
        CheckText.Foreground = empty ? Visuals.Resource<IBrush>("T.Muted") : check.Status == NestCheckStatus.Found ? brush : Visuals.Resource<IBrush>("T.Text");
        DrawMethods();
    }

    /// <summary>Shows the ways into the nest that was found: Google, email, and the browser for the rest.</summary>
    private void DrawMethods()
    {
        var can = _check is { CanSignIn: true };
        var methods = _check?.Hello?.Methods;
        var google = can && methods?.Google == true;
        var email = can && methods?.Email == true;
        GoogleButton.IsVisible = google;
        GoogleButton.IsEnabled = google;
        OrRow.IsVisible = google && email;
        EmailPanel.IsVisible = email;
        EmailBox.IsEnabled = email;
        EmailButton.IsEnabled = email && Nest.LooksLikeEmail(EmailBox.Text);
        // With no button of its own on show, the browser is the one way in and takes the accent.
        BrowserButton.Content = google || email ? "More ways to sign in in your browser" : "Sign in with your browser";
        BrowserButton.Classes.Set("accent", !(google || email));
        BrowserButton.Classes.Set("subtle", google || email);
        BrowserButton.IsEnabled = can;
    }

    private void OnSignIn(object? sender, RoutedEventArgs e) => StartChecked(null, null);

    private void OnGoogle(object? sender, RoutedEventArgs e) => StartChecked("google", null);

    private void OnEmail(object? sender, RoutedEventArgs e)
    {
        if (Nest.LooksLikeEmail(EmailBox.Text))
            StartChecked("email", (EmailBox.Text ?? string.Empty).Trim());
    }

    private void StartChecked(string? method, string? email)
    {
        if (_check is not { CanSignIn: true, Url: { } url })
            return;
        _nest = url;
        StartFlow(url, ThisName(), method, email);
    }

    // ------------------------------------------------------------------ 5. waiting for approval

    private void StartFlow(Uri url, string name, string? method, string? email)
    {
        _flowStop?.Cancel();
        var stop = _flowStop = new CancellationTokenSource();
        _browserOpened = false;
        (_method, _email) = (method, email);
        var flow = new PairingFlow(url, name, method, email);
        flow.Changed += s => Dispatcher.UIThread.Post(() =>
        {
            if (!stop.IsCancellationRequested)
                ShowPairing(s);
        });
        ShowPairing(new PairingState(PairingStage.Starting));
        _ = Task.Run(() => flow.RunAsync(stop.Token));
    }

    /// <summary>Draws the waiting step for a state of the flow (also used by the screenshot test).</summary>
    internal void ShowPairing(PairingState state, bool openBrowser = true)
    {
        _state = state;
        if (state.Stage == PairingStage.Approved && state.Grant is { } grant)
        {
            _clock.Stop();
            ShowFolderStep(grant, _nest);
            return;
        }
        ShowStep(SignInStep.Wait);
        CodeText.Text = state.Code ?? "····-····";
        if (state.Stage == PairingStage.Waiting && openBrowser && !_browserOpened && state.VerifyUrl is { } link)
        {
            _browserOpened = true;
            _openUrl(link);
        }
        var finished = state.IsFinished;
        WaitRow.IsVisible = !finished;
        WaitHint.IsVisible = !finished;
        OpenAgainButton.IsVisible = !finished && state.VerifyUrl is not null;
        CopyButton.IsVisible = !finished && state.VerifyUrl is not null;
        RetryButton.IsVisible = finished;
        RetryButton.Content = state.Stage == PairingStage.Denied ? "Start over" : "Get a new code";
        BackButton.Content = finished ? "Back" : "Cancel";
        OutcomeBox.IsVisible = finished || state.Message is not null;
        OutcomeText.Text = state.Stage switch
        {
            PairingStage.Denied => "Your nest said no. If that was a mistake, start over and allow it this time.",
            PairingStage.Expired => "The code expired before it was approved (codes last 10 minutes).",
            _ => state.Message ?? string.Empty,
        };
        WaitIntro.Text = state.Stage == PairingStage.Starting
            ? "Asking your nest for a code…"
            : "Your browser opened your nest. Sign in there and check that it shows this code:";
        if (finished)
            _clock.Stop();
        else
            _clock.Start();
        DrawWaiting();
    }

    /// <summary>The countdown under the code, on whichever waiting step is on show.</summary>
    private void DrawWaiting()
    {
        if (Step == SignInStep.AccountWait && _account is { } account)
        {
            var left = account.ExpiresText(DateTimeOffset.UtcNow);
            AccountExpiresText.Text = left.Length == 0 ? string.Empty : char.ToUpperInvariant(left[0]) + left[1..];
            return;
        }
        if (_state is not { } state)
            return;
        WaitText.Text = state.Stage == PairingStage.Starting ? "Asking your nest…" : $"Waiting for approval… {state.ExpiresText(DateTimeOffset.UtcNow)}";
    }

    private void OnOpenAgain(object? sender, RoutedEventArgs e)
    {
        if (_state?.VerifyUrl is { } link)
            _openUrl(link);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_state?.VerifyUrl is { } link && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(link);
            CopyButton.Content = "Copied";
        }
    }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        if (_nest is { } url)
            StartFlow(url, ThisName(), _method, _email);
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        _flowStop?.Cancel();
        _clock.Stop();
        ShowStep(SignInStep.Address);
    }

    // ------------------------------------------------------------------ 6. the folder

    /// <summary>Signed in on the nest: on to the folder (also used by the screenshot test).</summary>
    internal void ShowFolderStep(DeviceKeyGrant grant, Uri? nest)
    {
        _state = new PairingState(PairingStage.Approved, Grant: grant);
        _accountResult = null;
        _nest = nest;
        ShowStep(SignInStep.Folder);
        SignedInText.Text = $"✓ Signed in as {grant.Name}" + (nest is null ? string.Empty : $" on {nest.Host}");
    }

    /// <summary>Signed in with the account: on to the folder, for the nest the account chose.</summary>
    internal void ShowFolderStep(AccountSignInResult result)
    {
        ShowFolderStep(result.Device, result.ServerUrl);
        _accountResult = result;
        SignedInText.Text = $"✓ Signed in as {result.Device.Name} on {result.NestLabel}";
    }

    /// <summary>The folder field (screenshot test).</summary>
    internal string? Folder
    {
        get => FolderBox.Text;
        set => FolderBox.Text = value;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner)
            return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder to keep in sync", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            FolderBox.Text = path;
    }

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_state?.Grant is not { } grant || _nest is not { } nest)
            return;
        var folder = (FolderBox.Text ?? string.Empty).Trim();
        if (folder.StartsWith('~'))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.TrimStart('~', '/'));
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", "Choose the folder to keep in sync.");
            return;
        }
        StartButton.IsEnabled = false;
        try
        {
            if (!Directory.Exists(folder))
            {
                if (!await Dialogs.ConfirmAsync(Owner, "Pairnets", $"The folder {folder} does not exist. Create it?"))
                    return;
                Directory.CreateDirectory(folder);
            }
            try
            {
                using var api = new PairnetsApiClient(nest, grant.Key, grant.Name);
                var preview = await FirstSyncPreview.ComputeAsync(folder, new IgnoreList(_current.ExtraIgnore), api, CancellationToken.None);
                if (preview.IsMerge && !await Dialogs.ConfirmAsync(Owner, "Pairnets – merge folders",
                        $"This folder has {preview.LocalFiles} file(s) and your nest has {preview.ServerFiles}.\n\n{FirstSyncPreview.MergeExplanation}\n\nContinue?"))
                    return;
            }
            catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException or PairnetsAuthException)
            {
                // The preview is a courtesy; syncing itself never deletes or overwrites on a first pass.
            }
            var protectedKey = _protector?.Protect(grant.Key) ?? throw new InvalidOperationException("No secret store to keep this computer's key in.");
            var startAtLogin = AutoStartBox.IsChecked == true;
            var settings = _accountResult is { } account
                ? AccountSignIn.SettingsAfterSignIn(_current, account, folder, startAtLogin, protectedKey)
                : Nest.SettingsAfterSignIn(_current, nest, grant, folder, startAtLogin, protectedKey);
            SignedIn?.Invoke(settings, grant.Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", ex.Message);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
