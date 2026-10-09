using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Setting up a computer by signing in: type your nest, approve this computer in the browser, pick the
/// folder. The steps and their network work live in <see cref="PairingFlow"/> and <see cref="Nest"/>;
/// this view only draws them.
/// </summary>
public partial class SignInView : UserControl
{
    private readonly ClientSettings _current;
    private readonly ISecretProtector? _protector;
    private readonly Action<string> _openUrl;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _checking;
    private CancellationTokenSource? _flowStop;
    private NestCheck? _check;
    private PairingState? _state;
    private Uri? _nest;
    private string? _method;
    private string? _email;
    private bool _browserOpened;
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
        _clock.Tick += (_, _) => DrawWaiting();
        DetachedFromVisualTree += (_, _) =>
        {
            _clock.Stop();
            _flowStop?.Cancel();
            _checking?.Cancel();
        };
        if (!string.IsNullOrWhiteSpace(AddressBox.Text))
            CheckSoon(TimeSpan.Zero);
    }

    /// <summary>Signed in and a folder chosen: the settings to save (key protected) and the plain key for this session.</summary>
    public event Action<ClientSettings, string>? SignedIn;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>
    /// The nest this computer used before (signing in again), as people type it. An old install may still
    /// point at a plain address; the nest's own name, learned while syncing, then fills the field instead.
    /// </summary>
    private static string SuggestedAddress(ClientSettings current, string? nestHint) =>
        Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url.IsDefaultPort ? url.Host : url.Authority
            : Uri.TryCreate(nestHint, UriKind.Absolute, out var nest) && nest.Scheme == Uri.UriSchemeHttps
                ? nest.IsDefaultPort ? nest.Host : nest.Authority
                : string.Empty;

    // ------------------------------------------------------------------ 1. which nest

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

    private string ThisName() => _current.DeviceName ?? Environment.MachineName;

    // ------------------------------------------------------------------ 2. waiting for approval

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
        WelcomeStep.IsVisible = false;
        FolderStep.IsVisible = false;
        WaitStep.IsVisible = true;
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

    private void DrawWaiting()
    {
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
        WaitStep.IsVisible = false;
        WelcomeStep.IsVisible = true;
    }

    // ------------------------------------------------------------------ 3. the folder

    /// <summary>Signed in: on to the folder (also used by the screenshot test).</summary>
    internal void ShowFolderStep(DeviceKeyGrant grant, Uri? nest)
    {
        _state = new PairingState(PairingStage.Approved, Grant: grant);
        _nest = nest;
        WelcomeStep.IsVisible = false;
        WaitStep.IsVisible = false;
        FolderStep.IsVisible = true;
        SignedInText.Text = $"✓ Signed in as {grant.Name}" + (nest is null ? string.Empty : $" on {nest.Host}");
    }

    /// <summary>The folder field (screenshot test).</summary>
    internal string? Folder
    {
        get => FolderBox.Text;
        set => FolderBox.Text = value;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(Owner, "Choose the folder to keep in sync") is { } path)
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
            var settings = Nest.SettingsAfterSignIn(_current, nest, grant, folder, AutoStartBox.IsChecked == true, protectedKey);
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
