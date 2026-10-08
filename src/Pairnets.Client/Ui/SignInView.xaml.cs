using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Ui;

/// <summary>
/// Setting up a computer by signing in: type your nest, approve this computer in the browser, pick the
/// folder. The steps and their network work live in <see cref="PairingFlow"/> and <see cref="Nest"/>;
/// this view only draws them (the Mac/Linux app has the same view in Avalonia).
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

    public SignInView(ClientSettings current, ISecretProtector? protector, Action<string> openUrl, string? nestHint = null)
    {
        InitializeComponent();
        _current = current;
        _protector = protector;
        _openUrl = openUrl;
        AppIcon.Source = AppIcons.Large;
        FolderBox.Text = current.Folder ?? string.Empty;
        AutoStartBox.IsChecked = current.StartWithWindows || !current.FirstRunCompleted;
        AddressBox.Text = SuggestedAddress(current, nestHint);
        AddressBox.TextChanged += (_, _) =>
        {
            if (!_quiet)
                CheckSoon();
        };
        EmailBox.TextChanged += (_, _) =>
        {
            EmailButton.IsEnabled = _check is { CanSignIn: true } && Nest.LooksLikeEmail(EmailBox.Text);
        };
        _clock.Tick += (_, _) => DrawWaiting();
        IsVisibleChanged += (_, _) => SpinWhileWaiting();
        Unloaded += (_, _) =>
        {
            Motion.Spin(WaitIcon, false);
            _clock.Stop();
            _flowStop?.Cancel();
            _checking?.Cancel();
        };
        if (!string.IsNullOrWhiteSpace(AddressBox.Text))
            CheckSoon(TimeSpan.Zero);
    }

    /// <summary>Signed in and a folder chosen: the settings to save (key protected) and the plain key for this session.</summary>
    public event Action<ClientSettings, string>? SignedIn;

    private Window? Owner => Window.GetWindow(this);

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
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && !string.IsNullOrWhiteSpace(text))
                        ShowCheck(new NestCheck(NestCheckStatus.Empty, null, null, "Looking for your nest…"));
                });
                var check = await Nest.CheckAsync(text, ct: cts.Token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
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

    /// <summary>Shows an address and what was found there, without looking it up (screenshot tool).</summary>
    public void ShowAddress(string text, NestCheck check)
    {
        _checking?.Cancel();
        _quiet = true;
        AddressBox.Text = text;
        _quiet = false;
        ShowCheck(check);
    }

    /// <summary>Draws the line under "Your nest".</summary>
    public void ShowCheck(NestCheck check)
    {
        _check = check;
        CheckRow.Visibility = check.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckText.Text = check.Message;
        var (icon, brush) = check.Status switch
        {
            NestCheckStatus.Found => ("I.Check", "S.Green"),
            NestCheckStatus.Unreachable or NestCheckStatus.Invalid or NestCheckStatus.NotPairnets => ("I.X", "S.Red"),
            NestCheckStatus.NoSignIn => ("I.Info", "S.Orange"),
            _ => ("I.Wait", "T.Muted"),
        };
        CheckIcon.Data = Visuals.Resource<Geometry>(icon);
        CheckIcon.SetResourceReference(LineIcon.StrokeProperty, brush);
        CheckText.SetResourceReference(TextBlock.ForegroundProperty, check.Status == NestCheckStatus.Found ? brush
            : check.Status == NestCheckStatus.Empty ? "T.Muted" : "T.Text");
        DrawMethods();
    }

    /// <summary>Shows the ways into the nest that was found: Google, email, and the browser for the rest.</summary>
    private void DrawMethods()
    {
        var can = _check is { CanSignIn: true };
        var methods = _check?.Hello?.Methods;
        var google = can && methods?.Google == true;
        var email = can && methods?.Email == true;
        GoogleButton.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
        GoogleButton.IsEnabled = google;
        OrRow.Visibility = google && email ? Visibility.Visible : Visibility.Collapsed;
        EmailPanel.Visibility = email ? Visibility.Visible : Visibility.Collapsed;
        EmailBox.IsEnabled = email;
        EmailButton.IsEnabled = email && Nest.LooksLikeEmail(EmailBox.Text);
        // With no button of its own on show, the browser is the one way in and takes the accent.
        BrowserButton.Content = google || email ? "More ways to sign in in your browser" : "Sign in with your browser";
        BrowserButton.SetResourceReference(StyleProperty, google || email ? "FlatButton" : "AccentButton");
        BrowserButton.IsEnabled = can;
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => StartChecked(null, null);

    private void OnGoogle(object sender, RoutedEventArgs e) => StartChecked("google", null);

    private void OnEmail(object sender, RoutedEventArgs e)
    {
        if (Nest.LooksLikeEmail(EmailBox.Text))
            StartChecked("email", EmailBox.Text.Trim());
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
        flow.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            if (!stop.IsCancellationRequested)
                ShowPairing(s);
        });
        ShowPairing(new PairingState(PairingStage.Starting));
        _ = Task.Run(() => flow.RunAsync(stop.Token));
    }

    /// <summary>Draws the waiting step for a state of the flow.</summary>
    public void ShowPairing(PairingState state, bool openBrowser = true)
    {
        _state = state;
        if (state.Stage == PairingStage.Approved && state.Grant is { } grant)
        {
            _clock.Stop();
            ShowFolderStep(grant, _nest);
            return;
        }
        WelcomeStep.Visibility = Visibility.Collapsed;
        FolderStep.Visibility = Visibility.Collapsed;
        WaitStep.Visibility = Visibility.Visible;
        CodeText.Text = Spaced(state.Code ?? "····-····");
        if (state.Stage == PairingStage.Waiting && openBrowser && !_browserOpened && state.VerifyUrl is { } link)
        {
            _browserOpened = true;
            _openUrl(link);
        }
        var finished = state.IsFinished;
        var waiting = finished ? Visibility.Collapsed : Visibility.Visible;
        WaitRow.Visibility = waiting;
        WaitHint.Visibility = waiting;
        OpenAgainButton.Visibility = !finished && state.VerifyUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = OpenAgainButton.Visibility;
        RetryButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Content = state.Stage == PairingStage.Denied ? "Start over" : "Get a new code";
        BackButton.Content = finished ? "Back" : "Cancel";
        OutcomeBox.Visibility = finished || state.Message is not null ? Visibility.Visible : Visibility.Collapsed;
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
        SpinWhileWaiting();
        DrawWaiting();
    }

    /// <summary>
    /// The code with a thin space between its characters, so it is easy to compare (the Mac/Linux app spaces the
    /// letters out; WPF text has no letter spacing).
    /// </summary>
    private static string Spaced(string code) => string.Join(ThinSpace, code.Select(c => c.ToString()));

    private static readonly string ThinSpace = ((char)0x2009).ToString();

    /// <summary>The arrows next to "Waiting for approval…" turn while the step shows.</summary>
    private void SpinWhileWaiting() =>
        Motion.Spin(WaitIcon, IsVisible && WaitStep.Visibility == Visibility.Visible && WaitRow.Visibility == Visibility.Visible);

    private void DrawWaiting()
    {
        if (_state is not { } state)
            return;
        WaitText.Text = state.Stage == PairingStage.Starting ? "Asking your nest…" : $"Waiting for approval… {state.ExpiresText(DateTimeOffset.UtcNow)}";
    }

    private void OnOpenAgain(object sender, RoutedEventArgs e)
    {
        if (_state?.VerifyUrl is { } link)
            _openUrl(link);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_state?.VerifyUrl is not { } link)
            return;
        try
        {
            Clipboard.SetText(link);
            CopyButton.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; the button can be pressed again.
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_nest is { } url)
            StartFlow(url, ThisName(), _method, _email);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _flowStop?.Cancel();
        _clock.Stop();
        WaitStep.Visibility = Visibility.Collapsed;
        WelcomeStep.Visibility = Visibility.Visible;
        SpinWhileWaiting();
    }

    // ------------------------------------------------------------------ 3. the folder

    /// <summary>Signed in: on to the folder.</summary>
    public void ShowFolderStep(DeviceKeyGrant grant, Uri? nest)
    {
        _state = new PairingState(PairingStage.Approved, Grant: grant);
        _nest = nest;
        WelcomeStep.Visibility = Visibility.Collapsed;
        WaitStep.Visibility = Visibility.Collapsed;
        FolderStep.Visibility = Visibility.Visible;
        SpinWhileWaiting();
        SignedInText.Text = $"Signed in as {grant.Name}" + (nest is null ? string.Empty : $" on {nest.Host}");
    }

    /// <summary>The folder field (screenshot tool).</summary>
    public string Folder
    {
        get => FolderBox.Text;
        set => FolderBox.Text = value;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder to keep in sync", Multiselect = false };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if ((Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_state?.Grant is not { } grant || _nest is not { } nest)
            return;
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            Ask("Choose the folder to keep in sync.", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartButton.IsEnabled = false;
        try
        {
            if (!Directory.Exists(folder))
            {
                if (Ask($"The folder {folder} does not exist. Create it?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                Directory.CreateDirectory(folder);
            }
            try
            {
                using var api = new PairnetsApiClient(nest, grant.Key, grant.Name);
                var preview = await FirstSyncPreview.ComputeAsync(folder, new IgnoreList(_current.ExtraIgnore), api, CancellationToken.None);
                if (preview.IsMerge && Ask($"This folder has {preview.LocalFiles} file(s) and your nest has {preview.ServerFiles}.\n\n{FirstSyncPreview.MergeExplanation}\n\nContinue?",
                        MessageBoxButton.OKCancel, MessageBoxImage.Information, "Pairnets – merge folders") != MessageBoxResult.OK)
                    return;
            }
            catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException or PairnetsAuthException)
            {
                // The preview is a courtesy; syncing itself never deletes or overwrites on a first pass.
            }
            var protectedKey = _protector?.Protect(grant.Key) ?? throw new InvalidOperationException("No secret store to keep this computer's key in.");
            SignedIn?.Invoke(Nest.SettingsAfterSignIn(_current, nest, grant, folder, AutoStartBox.IsChecked == true, protectedKey), grant.Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ask(ex.Message, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage image, string title = "Pairnets") =>
        Owner is { } owner ? MessageBox.Show(owner, text, title, buttons, image) : MessageBox.Show(text, title, buttons, image);
}
