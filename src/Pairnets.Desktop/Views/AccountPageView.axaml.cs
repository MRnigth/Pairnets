using Avalonia.Controls;
using Avalonia.Interactivity;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The Account page: who is signed in on this computer (from <see cref="AccountSummary"/>), and the notification switches,
/// which are saved the moment they are changed (<see cref="IMainActions.SetNotifies"/>).
/// </summary>
public partial class AccountPageView : UserControl
{
    private IMainActions? _actions;
    private bool _drawing;

    public AccountPageView()
    {
        InitializeComponent();
    }

    /// <summary>What the buttons and switches do (the app's controller).</summary>
    public void Attach(IMainActions? actions)
    {
        _actions = actions;
        ShowSwitches();
    }

    /// <summary>Draws the page for this status (the latest one the window has).</summary>
    public void Show(StatusSnapshot status, string? thisDevice)
    {
        var a = AccountSummary.Build(status, thisDevice, null);
        InitialText.Text = a.Initial;
        TitleText.Text = a.Title;
        SubtitleText.Text = a.Email is null ? "On " + (a.NestHost ?? "your nest") : a.Subtitle;
        MethodText.Text = a.MethodName;
        AddedText.Text = a.AddedText;
        DeviceText.Text = a.DeviceName;
        NestText.Text = a.NestHost ?? "–";
        ShowSwitches();
    }

    private void ShowSwitches()
    {
        _drawing = true;
        try
        {
            AttentionSwitch.IsChecked = _actions?.Notifies(NoticeKind.Attention) ?? true;
            JoinSwitch.IsChecked = _actions?.Notifies(NoticeKind.JoinRequest) ?? true;
            UpdateSwitch.IsChecked = _actions?.Notifies(NoticeKind.Update) ?? true;
        }
        finally
        {
            _drawing = false;
        }
    }

    private void OnNotifyChanged(object? sender, RoutedEventArgs e)
    {
        if (_drawing || sender is not ToggleSwitch box)
            return;
        var kind = box == AttentionSwitch ? NoticeKind.Attention : box == JoinSwitch ? NoticeKind.JoinRequest : NoticeKind.Update;
        _actions?.SetNotifies(kind, box.IsChecked == true);
    }

    private void OnManageAccount(object? sender, RoutedEventArgs e) => _actions?.OpenAccountSite();

    private void OnSignOut(object? sender, RoutedEventArgs e) => _actions?.SignOut();

    // Exposed for the headless UI test.
    internal string TitleValue => TitleText.Text ?? string.Empty;
    internal string SubtitleValue => SubtitleText.Text ?? string.Empty;
    internal string MethodValue => MethodText.Text ?? string.Empty;
    internal string AddedValue => AddedText.Text ?? string.Empty;
    internal string DeviceValue => DeviceText.Text ?? string.Empty;
    internal string NestValue => NestText.Text ?? string.Empty;

    internal ToggleSwitch NotifySwitch(NoticeKind kind) => kind switch
    {
        NoticeKind.Attention => AttentionSwitch,
        NoticeKind.JoinRequest => JoinSwitch,
        _ => UpdateSwitch,
    };
}
