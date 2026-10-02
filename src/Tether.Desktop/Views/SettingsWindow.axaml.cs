using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tether.Core.Api;
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

    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool firstRun, bool autoStart)
    {
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
    }

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
