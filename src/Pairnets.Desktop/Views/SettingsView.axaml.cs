using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The settings form: the main window's Settings page. Setting up a computer is the sign-in window
/// (<see cref="SignInView"/>), never this form. Validation and preview logic live in Pairnets.Core.
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly ClientSettings _original;
    private readonly ISecretProtector? _protector;

    public SettingsView()
        : this(new ClientSettings(), null, autoStart: false)
    {
    }

    /// <summary>Raised when the settings were checked and should be saved (<see cref="Result"/>, <see cref="PlainToken"/>).</summary>
    public event Action<SettingsView>? Saved;

    /// <summary>Raised when the person leaves without saving.</summary>
    public event Action? Cancelled;

    /// <summary>The window this form is in, as the owner of its dialogs.</summary>
    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private readonly UpdateService? _updates;

    /// <summary>How far one wheel notch scrolls (the default is too fast for this window).</summary>
    private const double WheelPixelsPerNotch = 16;

    private void OnScrollWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        Scroller.Offset = Scroller.Offset.WithY(Math.Clamp(Scroller.Offset.Y - e.Delta.Y * WheelPixelsPerNotch,
            0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
        e.Handled = true;
    }

    public SettingsView(ClientSettings current, ISecretProtector? protector, bool autoStart, UpdateService? updates = null, string? serverVersionText = null)
    {
        _updates = updates;
        InitializeComponent();
        _original = current;
        _protector = protector;
        if (current.HasOwnKey)
        {
            // Signed in with its own key: show who it is; the address and token stay one click away.
            ServerHeading.Text = "Your nest";
            SignedInPanel.IsVisible = true;
            AddressFields.IsVisible = false;
            SignedInText.Text = $"{(Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var nest) ? nest.Authority : current.ServerUrl)} · signed in as {current.DeviceName}";
        }
        ServerUrlBox.Text = current.ServerUrl ?? string.Empty;
        TokenBox.Watermark = current.ProtectedToken is null ? "printed by install.sh" : "(saved – leave empty to keep)";
        FolderBox.Text = current.Folder ?? string.Empty;
        DeviceBox.Text = current.DeviceName ?? Environment.MachineName;
        IgnoreBox.Text = string.Join(Environment.NewLine, current.ExtraIgnore);
        AutoStartBox.IsChecked = autoStart;
        UpdateBox.IsChecked = current.CheckForUpdates;
        WaitBox.IsChecked = current.WaitForPeerBatches;
        AutoServerBox.IsChecked = current.AutoUpdateServer;
        DebugBox.IsChecked = current.DebugMode;
        ServerVersionText.Text = serverVersionText ?? "Server: not connected yet";
        (current.EffectiveParallelTransfers switch { 1 => Par1, 2 => Par2, 8 => Par8, _ => Par4 }).IsChecked = true;
        UpLimitBox.IsChecked = current.UploadLimitMBps is > 0;
        UpLimitValue.Value = (decimal)(current.UploadLimitMBps is > 0 ? current.UploadLimitMBps.Value : 5);
        DownLimitBox.IsChecked = current.DownloadLimitMBps is > 0;
        DownLimitValue.Value = (decimal)(current.DownloadLimitMBps is > 0 ? current.DownloadLimitMBps.Value : 10);
        AboutText.Text = PairnetsLinks.AboutLine;
        ShowVersion(null);
    }

    /// <summary>"You have 1.0.52 · checked 19:41" plus the outcome of "Check now".</summary>
    public void ShowVersion(string? outcome)
    {
        var text = "You have " + PairnetsInfo.ProductVersion;
        if (_updates?.LastChecked is { } at)
            text += " · checked " + at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (outcome is not null)
            text += " · " + outcome;
        VersionText.Text = text;
        CheckNowButton.IsVisible = _updates is not null;
    }

    private async void OnCheckNow(object? sender, RoutedEventArgs e)
    {
        if (_updates is null)
            return;
        CheckNowButton.IsEnabled = false;
        ShowVersion("checking…");
        var found = await _updates.CheckNowAsync(CancellationToken.None);
        ShowVersion(found is null ? "up to date" : $"version {found.Version} is available");
        CheckNowButton.IsEnabled = true;
    }

    private int ParallelChoice() => Par1.IsChecked == true ? 1 : Par2.IsChecked == true ? 2 : Par8.IsChecked == true ? 8 : 4;

    private static double? Limit(CheckBox box, NumericUpDown value) =>
        box.IsChecked == true && value.Value is { } v && v > 0 ? (double)v : null;

    /// <summary>The settings to save, when the window closes with a result.</summary>
    public ClientSettings? Result { get; private set; }

    public string? PlainToken { get; private set; }

    private string? CurrentToken()
    {
        if (!string.IsNullOrWhiteSpace(TokenBox.Text))
            return TokenBox.Text.Trim();
        if (_original.ProtectedToken is null || _protector is null)
            return null;
        try
        {
            return _protector.Unprotect(_original.ProtectedToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string DeviceName() => string.IsNullOrWhiteSpace(DeviceBox.Text) ? Environment.MachineName : DeviceBox.Text.Trim();

    private List<string> Patterns() =>
        (IgnoreBox.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner)
            return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder to keep in sync", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            FolderBox.Text = path;
    }

    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        ShowTestResult(null, "Testing…");
        try
        {
            var result = await PairnetsApiClient.TestConnectionAsync(ServerUrlBox.Text, CurrentToken(), DeviceName());
            ShowTestResult(result.Status == ConnectionTestStatus.Ok, result.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Shows the connection test result as a green (ok), red (failed) or grey (busy) chip.</summary>
    internal void ShowTestResult(bool? ok, string text)
    {
        TestChip.IsVisible = true;
        TestChip.Classes.Set("chip-ok", ok == true);
        TestChip.Classes.Set("chip-bad", ok == false);
        TestChip.Classes.Set("chip-busy", ok is null);
        TestResult.Foreground = ok switch
        {
            true => new SolidColorBrush(Color.FromRgb(46, 160, 67)),
            false => new SolidColorBrush(Color.FromRgb(207, 34, 46)),
            _ => Visuals.Resource<IBrush>("T.Muted"),
        };
        TestResult.Text = (ok == true ? "✓ " : ok == false ? "✕ " : string.Empty) + text;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancelled?.Invoke();

    /// <summary>"Sign out of this computer" (the controller asks first).</summary>
    public event Action? SignOutRequested;

    /// <summary>"Manage devices on the web".</summary>
    public event Action? ManageDevicesRequested;

    /// <summary>Scrolls to the bottom of the page (screenshot test).</summary>
    internal void ScrollToEnd() => Scroller.ScrollToEnd();

    /// <summary>"Reset this app…" (the controller asks first, and starts over from the sign-in window).</summary>
    public event Action? ResetRequested;

    private void OnReset(object? sender, RoutedEventArgs e) => ResetRequested?.Invoke();

    private void OnSignOut(object? sender, RoutedEventArgs e) => SignOutRequested?.Invoke();

    private void OnManageDevices(object? sender, RoutedEventArgs e) => ManageDevicesRequested?.Invoke();

    /// <summary>A link under "About Pairnets": the address of one of the website's pages (<see cref="PairnetsLinks"/>).</summary>
    public event Action<string>? OpenLinkRequested;

    private void OnAboutLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string url })
            OpenLinkRequested?.Invoke(url);
    }

    private void OnShowAddress(object? sender, RoutedEventArgs e)
    {
        AddressFields.IsVisible = true;
        ShowAddressButton.IsVisible = false;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!PairnetsApiClient.TryParseServerUrl(ServerUrlBox.Text, out var url) || url is null)
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", "Enter a server URL such as https://sync.example.com/");
            return;
        }
        var token = CurrentToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", "Enter the token printed by install.sh on the server.");
            return;
        }
        var folder = (FolderBox.Text ?? string.Empty).Trim();
        if (folder.StartsWith('~'))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.TrimStart('~', '/'));
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", "Choose the folder to sync.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            if (!await Dialogs.ConfirmAsync(Owner, "Pairnets", $"The folder {folder} does not exist. Create it?"))
                return;
            Directory.CreateDirectory(folder);
        }

        SaveButton.IsEnabled = false;
        try
        {
            var test = await PairnetsApiClient.TestConnectionAsync(url.ToString(), token, DeviceName());
            if (test.Status != ConnectionTestStatus.Ok)
            {
                if (!await Dialogs.ConfirmAsync(Owner, "Pairnets", test.Message + "\n\nSave these settings anyway?"))
                    return;
            }
            else if (!string.Equals(folder, _original.Folder, StringComparison.Ordinal))
            {
                using var api = new PairnetsApiClient(url, token, DeviceName());
                var preview = await FirstSyncPreview.ComputeAsync(folder, new IgnoreList(Patterns()), api, CancellationToken.None);
                if (preview.IsMerge && !await Dialogs.ConfirmAsync(Owner, "Pairnets – merge folders",
                        $"This folder has {preview.LocalFiles} file(s) and the server has {preview.ServerFiles}.\n\n{FirstSyncPreview.MergeExplanation}\n\nContinue?",
                        "Continue", "Cancel"))
                    return;
            }

            if (_protector is null)
                throw new InvalidOperationException("No secret store available.");
            var (deviceId, deviceName) = await OwnKey.CarryOverAsync(_original, url, token, tokenTyped: !string.IsNullOrWhiteSpace(TokenBox.Text), DeviceName());
            Result = new ClientSettings
            {
                ServerUrl = url.ToString(),
                ProtectedToken = _protector.Protect(token),
                Folder = folder,
                DeviceName = deviceName,
                DeviceId = deviceId,
                AccountEmail = OwnKey.AccountEmailAfterSave(_original, url, deviceId),
                ExtraIgnore = Patterns(),
                StartWithWindows = AutoStartBox.IsChecked == true,
                FirstRunCompleted = true,
                Paused = _original.Paused,
                CheckForUpdates = UpdateBox.IsChecked == true,
                WaitForPeerBatches = WaitBox.IsChecked == true,
                AutoUpdateServer = AutoServerBox.IsChecked == true,
                DebugMode = DebugBox.IsChecked == true,
                ParallelTransfers = ParallelChoice(),
                UploadLimitMBps = Limit(UpLimitBox, UpLimitValue),
                DownloadLimitMBps = Limit(DownLimitBox, DownLimitValue),
                SkippedServerVersion = _original.SkippedServerVersion,
            };
            PlainToken = token;
            Saved?.Invoke(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
