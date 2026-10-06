using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tether.Core;
using Tether.Core.Api;
using Tether.Core.Client;
using Tether.Core.Paths;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Desktop.Views;

/// <summary>Settings and first-run window. Validation and preview logic live in Tether.Core.</summary>
public partial class SettingsWindow : Window
{
    private readonly ClientSettings _original;
    private readonly ISecretProtector? _protector;
    private readonly bool _firstRun;

    public SettingsWindow()
        : this(new ClientSettings(), null, firstRun: true, autoStart: false)
    {
    }

    private readonly UpdateService? _updates;

    /// <summary>How far one wheel notch scrolls (the default is too fast for this window).</summary>
    private const double WheelPixelsPerNotch = 16;

    private void OnScrollWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        Scroller.Offset = Scroller.Offset.WithY(Math.Clamp(Scroller.Offset.Y - e.Delta.Y * WheelPixelsPerNotch,
            0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
        e.Handled = true;
    }

    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool firstRun, bool autoStart, UpdateService? updates = null, string? serverVersionText = null)
    {
        _updates = updates;
        InitializeComponent();
        _original = current;
        _protector = protector;
        _firstRun = firstRun;
        Title = firstRun ? "Tether – first-time setup" : "Tether – settings";
        WelcomeHeader.IsVisible = firstRun;
        SettingsHeader.IsVisible = !firstRun;
        SaveButton.Content = firstRun ? "Start syncing" : "Save";
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
        ShowVersion(null);
    }

    /// <summary>"You have 1.0.52 · checked 19:41" plus the outcome of "Check now".</summary>
    public void ShowVersion(string? outcome)
    {
        var text = "You have " + TetherInfo.ProductVersion;
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder to keep in sync", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            FolderBox.Text = path;
    }

    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        ShowTestResult(null, "Testing…");
        try
        {
            var result = await TetherApiClient.TestConnectionAsync(ServerUrlBox.Text, CurrentToken(), DeviceName());
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

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!TetherApiClient.TryParseServerUrl(ServerUrlBox.Text, out var url) || url is null)
        {
            await Dialogs.InfoAsync(this, "Tether", "Enter a server URL such as http://100.x.y.z:5075/");
            return;
        }
        var token = CurrentToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            await Dialogs.InfoAsync(this, "Tether", "Enter the token printed by install.sh on the server.");
            return;
        }
        var folder = (FolderBox.Text ?? string.Empty).Trim();
        if (folder.StartsWith('~'))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.TrimStart('~', '/'));
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            await Dialogs.InfoAsync(this, "Tether", "Choose the folder to sync.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            if (!await Dialogs.ConfirmAsync(this, "Tether", $"The folder {folder} does not exist. Create it?"))
                return;
            Directory.CreateDirectory(folder);
        }

        SaveButton.IsEnabled = false;
        try
        {
            var test = await TetherApiClient.TestConnectionAsync(url.ToString(), token, DeviceName());
            if (test.Status != ConnectionTestStatus.Ok)
            {
                if (!await Dialogs.ConfirmAsync(this, "Tether", test.Message + "\n\nSave these settings anyway?"))
                    return;
            }
            else if (_firstRun || !string.Equals(folder, _original.Folder, StringComparison.Ordinal))
            {
                using var api = new TetherApiClient(url, token, DeviceName());
                var preview = await FirstSyncPreview.ComputeAsync(folder, new IgnoreList(Patterns()), api, CancellationToken.None);
                if (preview.IsMerge && !await Dialogs.ConfirmAsync(this, "Tether – merge folders",
                        $"This folder has {preview.LocalFiles} file(s) and the server has {preview.ServerFiles}.\n\n{FirstSyncPreview.MergeExplanation}\n\nContinue?",
                        "Continue", "Cancel"))
                    return;
            }

            if (_protector is null)
                throw new InvalidOperationException("No secret store available.");
            Result = new ClientSettings
            {
                ServerUrl = url.ToString(),
                ProtectedToken = _protector.Protect(token),
                Folder = folder,
                DeviceName = DeviceName(),
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
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TetherNetworkException or TetherAuthException or TetherProtocolException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(this, "Tether", ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
