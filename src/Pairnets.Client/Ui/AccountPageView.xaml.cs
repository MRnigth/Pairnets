using System.Windows;
using System.Windows.Controls;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Account page (the left side's account button opens it): who is signed in, the Manage and Sign out buttons, four facts,
/// and the three notification switches. Each switch calls <see cref="IMainActions.SetNotifies"/> as soon as it is turned.
/// </summary>
public partial class AccountPageView : UserControl
{
    private bool _loading;

    public AccountPageView()
    {
        InitializeComponent();
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }

    /// <summary>The big line: the account's email, or this computer's name on an older nest.</summary>
    public string AccountTitleText => TitleText.Text;

    public string AccountSubtitleText => SubtitleText.Text;

    public string InitialText => Initial.Text;

    public string MethodText => MethodValue.Text;

    public string AddedText => AddedValue.Text;

    public string DeviceText => DeviceValue.Text;

    public string NestText => NestValue.Text;

    /// <summary>The switch of one kind of notification.</summary>
    public CheckBox NotifySwitch(NoticeKind kind) => kind switch
    {
        NoticeKind.JoinRequest => NotifyJoin,
        NoticeKind.Update => NotifyUpdate,
        _ => NotifyAttention,
    };

    /// <summary>Draws the page from the latest status and reads the switches from the app's settings.</summary>
    public void Show(StatusSnapshot s, string? device, string? serverUrl)
    {
        var account = AccountSummary.Build(s, device, serverUrl);
        Initial.Text = account.Initial;
        TitleText.Text = account.Title;
        SubtitleText.Text = account.Subtitle;
        MethodValue.Text = account.MethodName;
        AddedValue.Text = account.AddedText;
        DeviceValue.Text = account.DeviceName;
        NestValue.Text = account.NestHost ?? "–";
        ReadSwitches();
    }

    /// <summary>Sets the switches from the app (without telling the app again).</summary>
    public void ReadSwitches()
    {
        _loading = true;
        try
        {
            foreach (var kind in new[] { NoticeKind.Attention, NoticeKind.JoinRequest, NoticeKind.Update })
                NotifySwitch(kind).IsChecked = Host?.Actions?.Notifies(kind) ?? true;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnNotifyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box)
            return;
        var kind = box == NotifyJoin ? NoticeKind.JoinRequest : box == NotifyUpdate ? NoticeKind.Update : NoticeKind.Attention;
        Host?.Actions?.SetNotifies(kind, box.IsChecked == true);
    }

    private void OnManageAccount(object sender, RoutedEventArgs e) => Host?.Actions?.OpenAccountSite();

    private void OnSignOut(object sender, RoutedEventArgs e) => Host?.Actions?.SignOut();
}
