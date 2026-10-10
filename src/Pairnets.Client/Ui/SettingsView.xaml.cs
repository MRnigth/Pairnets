using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Ui;

/// <summary>
/// The settings form: the main window's Settings page. Setting up a computer is the sign-in window
/// (<see cref="SignInView"/>), never this form. All validation logic lives in Pairnets.Core.
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly ClientSettings _original;
    private readonly ISecretProtector _protector;

    private readonly UpdateService? _updates;

    /// <summary>How far one wheel notch scrolls (the Windows default of 3 lines is ~48 px, too fast here).</summary>
    private const double WheelPixelsPerNotch = 16;

    private void OnScrollWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 120.0 * WheelPixelsPerNotch);
        e.Handled = true;
    }

    /// <summary>Raised when the settings were checked and should be saved (<see cref="Result"/>, <see cref="PlainToken"/>).</summary>
    public event Action<SettingsView>? Saved;

    /// <summary>Raised when the person leaves without saving.</summary>
    public event Action? Cancelled;

    /// <summary>The window this form is in, as the owner of its dialogs.</summary>
    private Window? Owner => Window.GetWindow(this);

    public SettingsView(ClientSettings current, ISecretProtector protector, UpdateService? updates = null, string? serverVersionText = null)
    {
        _updates = updates;
        InitializeComponent();
        _original = current;
        _protector = protector;
        if (current.HasOwnKey)
        {
            // Signed in with its own key: show who it is; the address and token stay one click away.
            ServerHeading.Text = "Your nest";
            SignedInPanel.Visibility = Visibility.Visible;
            AddressFields.Visibility = Visibility.Collapsed;
            SignedInText.Text = $"{(Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var nest) ? nest.Authority : current.ServerUrl)} · signed in as {current.DeviceName}";
        }
        ServerUrlBox.Text = current.ServerUrl ?? string.Empty;
        FolderBox.Text = current.Folder ?? string.Empty;
        DeviceBox.Text = current.DeviceName ?? Environment.MachineName;
        IgnoreBox.Text = string.Join(Environment.NewLine, current.ExtraIgnore);
        AutoStartBox.IsChecked = current.StartWithWindows;
        UpdateBox.IsChecked = current.CheckForUpdates;
        WaitBox.IsChecked = current.WaitForPeerBatches;
        AutoServerBox.IsChecked = current.AutoUpdateServer;
        DebugBox.IsChecked = current.DebugMode;
        ServerVersionText.Text = serverVersionText ?? "Server: not connected yet";
        AboutText.Text = PairnetsLinks.AboutLine;
        (current.EffectiveParallelTransfers switch { 1 => Par1, 2 => Par2, 8 => Par8, _ => Par4 }).IsChecked = true;
        UpLimitBox.IsChecked = current.UploadLimitMBps is > 0;
        UpLimitValue.Text = Number(current.UploadLimitMBps is > 0 ? current.UploadLimitMBps.Value : 5);
        DownLimitBox.IsChecked = current.DownloadLimitMBps is > 0;
        DownLimitValue.Text = Number(current.DownloadLimitMBps is > 0 ? current.DownloadLimitMBps.Value : 10);
        ShowVersion(null);
    }

    private static string Number(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>"You have 1.0.52 · checked 19:41" plus the outcome of "Check now".</summary>
    public void ShowVersion(string? outcome)
    {
        var text = "You have " + PairnetsInfo.ProductVersion;
        if (_updates?.LastChecked is { } at)
            text += " · checked " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (outcome is not null)
            text += " · " + outcome;
        VersionText.Text = text;
        CheckNowButton.Visibility = _updates is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
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

    /// <summary>The limit in MB/s, null when off; false when the number cannot be read.</summary>
    private static bool TryLimit(CheckBox box, TextBox value, out double? limit)
    {
        limit = null;
        if (box.IsChecked != true)
            return true;
        if (!double.TryParse(value.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
            && !double.TryParse(value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return false;
        if (v < 0.1)
            return false;
        limit = v;
        return true;
    }

    /// <summary>The settings to save (token protected) once <see cref="Saved"/> is raised.</summary>
    public ClientSettings? Result { get; private set; }

    /// <summary>The plain token for this session (never written to disk in plain text).</summary>
    public string? PlainToken { get; private set; }

    private string? CurrentToken()
    {
        if (TokenBox.Password.Length > 0)
            return TokenBox.Password.Trim();
        if (_original.ProtectedToken is null)
            return null;
        try
        {
            return _protector.Unprotect(_original.ProtectedToken);
        }
        catch (Exception)
        {
            return null; // saved by another Windows user or corrupted: ask again
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder to keep in sync", Multiselect = false };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if ((Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
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
    public void ShowTestResult(bool? ok, string text)
    {
        TestChip.Visibility = Visibility.Visible;
        var (fg, bg) = ok switch
        {
            true => (Color.FromRgb(46, 160, 67), Color.FromArgb(40, 46, 160, 67)),
            false => (Color.FromRgb(207, 34, 46), Color.FromArgb(36, 207, 34, 46)),
            _ => (default(Color?), default(Color?)),
        };
        if (fg is { } f && bg is { } b)
        {
            TestResult.Foreground = new SolidColorBrush(f);
            TestChip.Background = new SolidColorBrush(b);
        }
        else
        {
            TestResult.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
            TestChip.SetResourceReference(Border.BackgroundProperty, "T.Pill");
        }
        TestResult.Text = (ok == true ? "✓ " : ok == false ? "✕ " : string.Empty) + text;
    }


    private string DeviceName() => string.IsNullOrWhiteSpace(DeviceBox.Text) ? Environment.MachineName : DeviceBox.Text.Trim();

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!PairnetsApiClient.TryParseServerUrl(ServerUrlBox.Text, out var url) || url is null)
        {
            Fail("Enter a server URL such as https://sync.example.com/");
            return;
        }
        var token = CurrentToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            Fail("Enter the token printed by install.sh on the server.");
            return;
        }
        if (!TryLimit(UpLimitBox, UpLimitValue, out var upLimit) || !TryLimit(DownLimitBox, DownLimitValue, out var downLimit))
        {
            Fail("Enter the speed limits in MB/s, for example 5 or 0.5 (at least 0.1).");
            return;
        }
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            Fail("Choose the folder to sync.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            if (Ask($"The folder {folder} does not exist. Create it?", "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            Directory.CreateDirectory(folder);
        }

        SaveButton.IsEnabled = false;
        try
        {
            var test = await PairnetsApiClient.TestConnectionAsync(url.ToString(), token, DeviceName());
            if (test.Status != ConnectionTestStatus.Ok)
            {
                if (Ask(test.Message + "\n\nSave these settings anyway?", "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            else if (!string.Equals(folder, _original.Folder, StringComparison.OrdinalIgnoreCase))
            {
                var extra = SplitPatterns();
                using var api = new PairnetsApiClient(url, token, DeviceName());
                var preview = await FirstSyncPreview.ComputeAsync(folder, new IgnoreList(extra), api, CancellationToken.None);
                if (preview.IsMerge && Ask(
                        $"This folder has {preview.LocalFiles} file(s) and the server has {preview.ServerFiles}.\n\n{FirstSyncPreview.MergeExplanation}\n\nContinue?",
                        "Pairnets – merge folders", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    return;
            }

            var (deviceId, deviceName) = await OwnKey.CarryOverAsync(_original, url, token, tokenTyped: TokenBox.Password.Length > 0, DeviceName());
            Result = new ClientSettings
            {
                ServerUrl = url.ToString(),
                ProtectedToken = _protector.Protect(token),
                Folder = folder,
                DeviceName = deviceName,
                DeviceId = deviceId,
                AccountEmail = OwnKey.AccountEmailAfterSave(_original, url, deviceId),
                ExtraIgnore = SplitPatterns(),
                StartWithWindows = AutoStartBox.IsChecked == true,
                FirstRunCompleted = true,
                Paused = _original.Paused,
                CheckForUpdates = UpdateBox.IsChecked == true,
                WaitForPeerBatches = WaitBox.IsChecked == true,
                AutoUpdateServer = AutoServerBox.IsChecked == true,
                DebugMode = DebugBox.IsChecked == true,
                ParallelTransfers = ParallelChoice(),
                UploadLimitMBps = upLimit,
                DownloadLimitMBps = downLimit,
                SkippedServerVersion = _original.SkippedServerVersion,
            };
            PlainToken = token;
            Saved?.Invoke(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            Fail(ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private List<string> SplitPatterns() =>
        IgnoreBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Fail(string message) => Ask(message, "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);

    private MessageBoxResult Ask(string text, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner ? MessageBox.Show(owner, text, title, buttons, image) : MessageBox.Show(text, title, buttons, image);

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    /// <summary>"Sign out of this computer" (the controller asks first).</summary>
    public event Action? SignOutRequested;

    /// <summary>"Manage devices on the web".</summary>
    public event Action? ManageDevicesRequested;

    /// <summary>Scrolls to the bottom of the page (screenshot tool).</summary>
    public void ScrollToEnd() => Scroller.ScrollToEnd();

    /// <summary>"Reset this app…" (the controller asks first, and starts over from the sign-in window).</summary>
    public event Action? ResetRequested;

    private void OnReset(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();

    private void OnSignOut(object sender, RoutedEventArgs e) => SignOutRequested?.Invoke();

    private void OnManageDevices(object sender, RoutedEventArgs e) => ManageDevicesRequested?.Invoke();

    /// <summary>A link under "About Pairnets": the address of one of the website's pages (<see cref="PairnetsLinks"/>).</summary>
    public event Action<string>? OpenLinkRequested;

    private void OnAboutLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string url })
            OpenLinkRequested?.Invoke(url);
    }

    private void OnShowAddress(object sender, RoutedEventArgs e)
    {
        AddressFields.Visibility = Visibility.Visible;
        ShowAddressButton.Visibility = Visibility.Collapsed;
    }
}
