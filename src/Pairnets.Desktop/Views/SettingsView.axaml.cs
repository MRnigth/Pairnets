using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    /// <summary>"Where your files live" as chosen on this page (saved with the rest).</summary>
    private readonly Dictionary<string, FilePlacement> _placement;

    /// <summary>The nest's files (the folders to choose for); null when not connected.</summary>
    private readonly INestFiles? _files;

    public SettingsView(ClientSettings current, ISecretProtector? protector, bool autoStart, UpdateService? updates = null, string? serverVersionText = null,
        INestFiles? files = null)
    {
        _updates = updates;
        _files = files;
        _placement = new Dictionary<string, FilePlacement>(current.Placement, StringComparer.Ordinal);
        InitializeComponent();
        _original = current;
        _protector = protector;
        var host = Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var nest) ? nest.Authority : current.ServerUrl;
        ConnectedPill.IsVisible = serverVersionText is not null;
        if (current.HasOwnKey)
        {
            // Signed in with its own key: show who it is; the address and token stay one click away.
            ServerHeading.Text = "Your nest";
            SignedInPanel.IsVisible = true;
            AddressFields.IsVisible = false;
            NestHostText.Text = host ?? "Not connected";
            var who = string.IsNullOrWhiteSpace(current.AccountEmail) ? current.DeviceName : current.AccountEmail;
            SignedInText.Text = "Signed in as " + who;
            AvatarText.Text = AvatarLetter(who);
        }
        ServerUrlBox.Text = current.ServerUrl ?? string.Empty;
        TokenBox.Watermark = current.ProtectedToken is null ? "printed by install.sh" : "(saved – leave empty to keep)";
        FolderBox.Text = current.Folder ?? string.Empty;
        DeviceBox.Text = current.DeviceName ?? ComputerName.Default();
        IgnoreBox.Text = string.Join(Environment.NewLine, current.ExtraIgnore);
        AutoStartBox.IsChecked = autoStart;
        FileManagerBox.IsChecked = current.ShowInFileManager;
        RightClickBox.IsChecked = current.ExplorerRightClick;
        RightClickRow.IsVisible = !OperatingSystem.IsMacOS(); // Finder has no right-click items from Pairnets
        TicksBox.IsChecked = current.CloudFolder != CloudFolderMode.Off;
        TicksRow.IsVisible = OperatingSystem.IsWindows(); // ticks are Windows' cloud files
        DrawPlacement();
        if (OperatingSystem.IsMacOS())
        {
            // Apple lets an app give its folder an icon, but not add it to Finder's sidebar: that stays a drag.
            FileManagerTitle.Text = "Give the Pairnets folder its icon";
            FileManagerText.Text = "The blue Pairnets folder icon in Finder. To keep the folder in Finder's sidebar, drag it there once.";
        }
        UpdateBox.IsChecked = current.CheckForUpdates;
        WaitBox.IsChecked = current.WaitForPeerBatches;
        MapSpeedBox.IsChecked = current.ShowMapSpeed;
        AutoServerBox.IsChecked = current.AutoUpdateServer;
        DebugBox.IsChecked = current.DebugMode;
        ServerVersionText.Text = $"Pairnets {PairnetsInfo.ProductVersion} for {SystemName()}"
            + (serverVersionText is { } v ? " · your nest runs " + (v.StartsWith("Nest ", StringComparison.Ordinal) ? v["Nest ".Length..] : v) : " · not connected to your nest yet");
        AppVersionText.Text = "Pairnets " + PairnetsInfo.ProductVersion;
        StartOverNote.Text = $"Your files in {(string.IsNullOrWhiteSpace(current.Folder) ? "your sync folder" : current.Folder)} stay where they are.";
        (current.EffectiveParallelTransfers switch { 1 => Par1, 2 => Par2, 8 => Par8, _ => Par4 }).IsChecked = true;
        UpLimitBox.IsChecked = current.UploadLimitMBps is > 0;
        UpLimitValue.Text = (current.UploadLimitMBps is > 0 ? current.UploadLimitMBps.Value : 5).ToString("0.##", CultureInfo.InvariantCulture);
        DownLimitBox.IsChecked = current.DownloadLimitMBps is > 0;
        DownLimitValue.Text = (current.DownloadLimitMBps is > 0 ? current.DownloadLimitMBps.Value : 10).ToString("0.##", CultureInfo.InvariantCulture);
        AboutText.Text = PairnetsLinks.AboutLine;
        ShowVersion(null);

        // Pieces that are wired here rather than in the XAML.
        IgnoreHeader.Click += OnToggleIgnore;
        IgnoreBox.TextChanged += OnIgnoreChanged;
        Scroller.SizeChanged += OnPageSized;
        ShowIgnoreCount();
        _applied = current.Clone();
        _applied.StartWithWindows = autoStart;
        WireSwitches();
    }

    // ------------------------------------------------------------------ switches apply at once

    /// <summary>The settings as the switches last applied them (the text boxes wait for Save).</summary>
    private ClientSettings? _applied;

    /// <summary>
    /// A switch (or the files-at-a-time choice, or a speed limit) changed: the settings with it, to apply at once. Only the
    /// switch-like values differ from the saved settings (<see cref="SettingsSwitches.WithSwitchesFrom"/>).
    /// </summary>
    public event Action<ClientSettings>? SwitchesChanged;

    private void WireSwitches()
    {
        foreach (var box in new[] { AutoStartBox, UpdateBox, WaitBox, MapSpeedBox, AutoServerBox, DebugBox, UpLimitBox, DownLimitBox, FileManagerBox, RightClickBox, TicksBox })
            box.IsCheckedChanged += OnSwitchChanged;
        foreach (var par in new[] { Par1, Par2, Par4, Par8 })
            par.IsCheckedChanged += OnParChanged;
        // A speed limit's number applies when it is left (or Enter): a number it cannot read waits for Save.
        foreach (var value in new[] { UpLimitValue, DownLimitValue })
        {
            value.LostFocus += OnLimitLeft;
            value.KeyDown += OnLimitKey;
        }
    }

    private void OnSwitchChanged(object? sender, RoutedEventArgs e) => ApplySwitches();

    private void OnParChanged(object? sender, RoutedEventArgs e) => ApplySwitches();

    private void OnLimitLeft(object? sender, RoutedEventArgs e) => ApplySwitches();

    private void OnLimitKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
            ApplySwitches();
    }

    /// <summary>True when what the switches show has been applied (the checks).</summary>
    internal bool SwitchesApplied => _applied is { } a && a.StartWithWindows == (AutoStartBox.IsChecked == true) && a.CheckForUpdates == (UpdateBox.IsChecked == true)
        && a.WaitForPeerBatches == (WaitBox.IsChecked == true) && a.ShowMapSpeed == (MapSpeedBox.IsChecked == true) && a.AutoUpdateServer == (AutoServerBox.IsChecked == true)
        && a.DebugMode == (DebugBox.IsChecked == true) && a.EffectiveParallelTransfers == ParallelChoice()
        && (a.UploadLimitMBps is not null) == (UpLimitBox.IsChecked == true) && (a.DownloadLimitMBps is not null) == (DownLimitBox.IsChecked == true)
        && a.ShowInFileManager == (FileManagerBox.IsChecked == true) && a.ExplorerRightClick == (RightClickBox.IsChecked == true)
        && a.CloudFolder == CloudChoice(a) && !SettingsSwitches.PlacementChanged(a, new ClientSettings { Placement = _placement });

    private void ApplySwitches()
    {
        if (_applied is null)
            return;
        if (!LimitReadable(UpLimitBox, UpLimitValue) || !LimitReadable(DownLimitBox, DownLimitValue))
            return;
        var next = _applied.Clone();
        next.StartWithWindows = AutoStartBox.IsChecked == true;
        next.CheckForUpdates = UpdateBox.IsChecked == true;
        next.WaitForPeerBatches = WaitBox.IsChecked == true;
        next.ShowMapSpeed = MapSpeedBox.IsChecked == true;
        next.AutoUpdateServer = AutoServerBox.IsChecked == true;
        next.DebugMode = DebugBox.IsChecked == true;
        next.ParallelTransfers = ParallelChoice();
        next.UploadLimitMBps = Limit(UpLimitBox, UpLimitValue);
        next.DownloadLimitMBps = Limit(DownLimitBox, DownLimitValue);
        next.ShowInFileManager = FileManagerBox.IsChecked == true;
        next.ExplorerRightClick = RightClickBox.IsChecked == true;
        next.CloudFolder = CloudChoice(_applied);
        next.Placement = new Dictionary<string, FilePlacement>(_placement, StringComparer.Ordinal);
        if (Same(next, _applied))
            return;
        _applied = next;
        SwitchesChanged?.Invoke(next);
    }

    /// <summary>False while a limit is on but its number cannot be read (that waits for Save).</summary>
    private static bool LimitReadable(ToggleSwitch box, TextBox value) => box.IsChecked != true || Limit(box, value) is not null;

    private static bool Same(ClientSettings a, ClientSettings b) =>
        a.StartWithWindows == b.StartWithWindows && a.CheckForUpdates == b.CheckForUpdates && a.WaitForPeerBatches == b.WaitForPeerBatches
        && a.ShowMapSpeed == b.ShowMapSpeed && a.AutoUpdateServer == b.AutoUpdateServer && a.DebugMode == b.DebugMode
        && a.EffectiveParallelTransfers == b.EffectiveParallelTransfers && !SettingsSwitches.LimitsChanged(a, b)
        && !SettingsSwitches.FileManagerChanged(a, b) && a.CloudFolder == b.CloudFolder && !SettingsSwitches.PlacementChanged(a, b);

    /// <summary>The sync ticks switch (Windows only: elsewhere the earlier value stays).</summary>
    private CloudFolderMode CloudChoice(ClientSettings earlier) => !OperatingSystem.IsWindows() ? earlier.CloudFolder
        : TicksBox.IsChecked == true ? CloudFolderMode.KeepAllHere : CloudFolderMode.Off;

    private static string AvatarLetter(string? text)
    {
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
                return char.ToUpperInvariant(c).ToString();
        }
        return "?";
    }

    // ------------------------------------------------------------------ where your files live

    /// <summary>The folders the card lists: "" (everything else), each top folder, and any folder with a choice of its own.</summary>
    public IReadOnlyList<string> PlacementFolders =>
        [string.Empty, .. (_files?.Folders() ?? []).Concat(_placement.Keys.Select(PlacementMap.NormalizeFolder).Where(k => k.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Where <paramref name="folder"/>'s files live as chosen on this page so far.</summary>
    public FilePlacement PlacementChosen(string folder) => new PlacementMap(_placement).For(folder);

    /// <summary>The choices as Save will save them.</summary>
    public IReadOnlyDictionary<string, FilePlacement> PlacementToSave => _placement;

    private void DrawPlacement()
    {
        PlacementRows.Children.Clear();
        PlacementRows.IsEnabled = _files is not null;
        PlacementRows.Opacity = _files is null ? 0.55 : 1;
        PlacementNote.Text = _files is null ? FilesPageText.SettingsOff : FilesPageText.SettingsText.Replace("this PC", "this computer", StringComparison.Ordinal);
        var tops = _files?.List(string.Empty) ?? [];
        foreach (var folder in PlacementFolders)
        {
            var name = folder.Length == 0 ? FilesPageText.EverythingElse : folder;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var size = folder.Length == 0 ? FilesPageText.EverythingElseNote
                : _files is null ? string.Empty
                : FilesPageText.SizeLine(tops.FirstOrDefault(e => e.IsFolder && e.Path == folder)?.Size ?? 0, _files.BytesHere(folder));
            row.Children.Add(new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = size, Classes = { "t3" } },
                },
            });
            var segments = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var now = PlacementChosen(folder);
            foreach (var choice in NestFiles.Choices)
            {
                var button = new RadioButton
                {
                    Content = NestFiles.PlacementText(choice),
                    Classes = { "seg" },
                    GroupName = "placement:" + folder,
                    IsChecked = choice == now,
                };
                Avalonia.Automation.AutomationProperties.SetName(button, $"{name}: {NestFiles.PlacementText(choice)}");
                button.IsCheckedChanged += (_, _) =>
                {
                    if (button.IsChecked == true)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ChoosePlacementAsync(folder, choice));
                };
                segments.Children.Add(button);
            }
            var frame = new Border { Classes = { "segments" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Child = segments };
            Grid.SetColumn(frame, 1);
            row.Children.Add(frame);
            PlacementRows.Children.Add(row);
        }
    }

    /// <summary>A choice in the card: asked first when files leave this computer, then applied at once like the switches.</summary>
    public async Task ChoosePlacementAsync(string folder, FilePlacement choice)
    {
        var now = PlacementChosen(folder);
        if (now == choice)
            return;
        if (await Dialogs.ConfirmAwayAsync(Owner, folder, now, choice, _files?.BytesHere(folder) ?? 0))
        {
            PlacementMap.Choose(_placement, folder, choice);
            ApplySwitches();
        }
        DrawPlacement(); // also puts the choice back when the person said no
    }

    /// <summary>"Choose for a folder inside these…": the Files page.</summary>
    public event Action? OpenFilesRequested;

    private void OnPlacementLink(object? sender, RoutedEventArgs e) => OpenFilesRequested?.Invoke();

    private static string SystemName() => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux";

    /// <summary>The page is two columns of cards; in a narrow window the right column goes below the left one.</summary>
    private void OnPageSized(object? sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 700;
        Columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RightColumn, narrow ? 0 : 1);
        Grid.SetRow(RightColumn, narrow ? 1 : 0);
        LeftColumn.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 8, 0);
        RightColumn.Margin = narrow ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        HelpGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void OnToggleIgnore(object? sender, RoutedEventArgs e)
    {
        IgnorePanel.IsVisible = !IgnorePanel.IsVisible;
        IgnoreChevron.Data = Visuals.Resource<Geometry>(IgnorePanel.IsVisible ? "I.ChevronUp" : "I.ChevronDown");
    }

    private void OnIgnoreChanged(object? sender, TextChangedEventArgs e) => ShowIgnoreCount();

    private void ShowIgnoreCount()
    {
        var n = Patterns().Count;
        IgnoreCount.Text = n == 1 ? "1 rule" : n + " rules";
    }

    /// <summary>"Report a bug…" in the Help row (the app opens the bug report window).</summary>
    public event Action? ReportBugRequested;

    private void OnReportBug(object? sender, RoutedEventArgs e) => ReportBugRequested?.Invoke();

    /// <summary>"You have 1.0.52 · checked 19:41" plus the outcome of "Check now".</summary>
    public void ShowVersion(string? outcome)
    {
        var checkedAt = _updates?.LastChecked is { } at ? "checked " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : null;
        VersionText.Text = (checkedAt, outcome) switch
        {
            (null, null) => "Not checked yet",
            (not null, null) => char.ToUpperInvariant(checkedAt[0]) + checkedAt[1..],
            (null, not null) => char.ToUpperInvariant(outcome[0]) + outcome[1..],
            _ => char.ToUpperInvariant(checkedAt![0]) + checkedAt[1..] + " · " + outcome,
        };
        CheckNowButton.IsVisible = _updates is not null;
    }

    private async void OnCheckNow(object? sender, RoutedEventArgs e)
    {
        if (_updates is null)
            return;
        CheckNowButton.IsEnabled = false;
        ShowVersion("checking…");
        var found = await _updates.CheckNowAsync(CancellationToken.None);
        ShowVersion(found is null ? "up to date"
            : found.Signed ? $"version {found.Version} is available"
            : $"version {found.Version} isn't signed by Pairnets, so it won't be installed");
        CheckNowButton.IsEnabled = true;
    }

    private int ParallelChoice() => Par1.IsChecked == true ? 1 : Par2.IsChecked == true ? 2 : Par8.IsChecked == true ? 8 : 4;

    private static double? Limit(ToggleSwitch box, TextBox value) =>
        box.IsChecked == true
        && double.TryParse((value.Text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

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

    private string DeviceName() => string.IsNullOrWhiteSpace(DeviceBox.Text) ? ComputerName.Default() : DeviceBox.Text.Trim();

    private List<string> Patterns() =>
        (IgnoreBox.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(Owner, "Choose the folder to keep in sync") is { } path)
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

    /// <summary>"Manage on the Computers page": the main window's page of computers (managing them lives there only).</summary>
    public event Action? ShowDevicesRequested;

    /// <summary>Scrolls to the bottom of the page (screenshot test).</summary>
    internal void ScrollToEnd() => Scroller.ScrollToEnd();

    /// <summary>"Reset this app…" (the controller asks first, and starts over from the sign-in window).</summary>
    public event Action? ResetRequested;

    private void OnReset(object? sender, RoutedEventArgs e) => ResetRequested?.Invoke();

    private void OnShowDevices(object? sender, RoutedEventArgs e) => ShowDevicesRequested?.Invoke();

    /// <summary>A link under "About Pairnets": the address of one of the website's pages (<see cref="PairnetsLinks"/>).</summary>
    public event Action<string>? OpenLinkRequested;

    private void OnAboutLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string url })
            OpenLinkRequested?.Invoke(url);
    }

    /// <summary>"Change…" next to where the nest keeps its files: the controller opens the window with the nest's drives.</summary>
    public event Action? ChangeStorageRequested;

    private void OnChangeStorage(object? sender, RoutedEventArgs e) => ChangeStorageRequested?.Invoke();

    /// <summary>
    /// The "Where your nest keeps files" row: "/mnt/data/pairnets · 3.60 TB free" with "Change…", shown only when the nest
    /// can move its files (null or an older nest: hidden).
    /// </summary>
    public void ShowNestStorage(NestStorage? storage)
    {
        var shown = storage is { CanMove: true };
        StoragePanel.IsVisible = shown;
        StorageText.Text = shown ? NestPlaceFlow.PlaceLine(storage!) : string.Empty;
    }

    /// <summary>The row's line as shown (tests), or null while it is hidden.</summary>
    internal string? StorageLine => StoragePanel.IsVisible ? StorageText.Text : null;

    /// <summary>"Move…" next to the program folder (Linux): the controller asks where to and moves the app.</summary>
    public event Action? MoveProgramRequested;

    private void OnMoveProgram(object? sender, RoutedEventArgs e) => MoveProgramRequested?.Invoke();

    /// <summary>The Linux app's "Program folder" row ("~/.local/opt/pairnets"); null hides it.</summary>
    public void ShowProgramFolder(string? folder)
    {
        ProgramPanel.IsVisible = folder is not null;
        ProgramText.Text = folder ?? string.Empty;
    }

    /// <summary>The program folder as shown (tests), or null while the row is hidden.</summary>
    internal string? ProgramLine => ProgramPanel.IsVisible ? ProgramText.Text : null;

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
                throw new PlainMessageException("No secret store available.");
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
                ShowInFileManager = FileManagerBox.IsChecked == true,
                ExplorerRightClick = RightClickBox.IsChecked == true,
                CloudFolder = CloudChoice(_original),
                Placement = new Dictionary<string, FilePlacement>(_placement, StringComparer.Ordinal),
                FirstRunCompleted = true,
                Paused = _original.Paused,
                SideFolded = _original.SideFolded,
                CheckForUpdates = UpdateBox.IsChecked == true,
                WaitForPeerBatches = WaitBox.IsChecked == true,
                ShowMapSpeed = MapSpeedBox.IsChecked == true,
                AutoUpdateServer = AutoServerBox.IsChecked == true,
                DebugMode = DebugBox.IsChecked == true,
                ParallelTransfers = ParallelChoice(),
                UploadLimitMBps = Limit(UpLimitBox, UpLimitValue),
                DownloadLimitMBps = Limit(DownLimitBox, DownLimitValue),
                SkippedServerVersion = _original.SkippedServerVersion,
                NotifyJoinRequests = _original.NotifyJoinRequests,
                NotifyAttention = _original.NotifyAttention,
                NotifyUpdates = _original.NotifyUpdates,
                NameOfferAnswered = _original.NameOfferAnswered,
            };
            if (OwnKey.NameNotSaved(_original, deviceId, DeviceName(), deviceName) is { } note)
                await Dialogs.InfoAsync(Owner, "Pairnets", note);
            PlainToken = token;
            Saved?.Invoke(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(Owner, "Pairnets", FriendlyErrors.Describe(ex));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
