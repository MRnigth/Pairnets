using System.Windows;
using System.Windows.Controls;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Devices page: the nest's card, then a grid of computer cards (<see cref="DeviceCardView"/>). It only draws
/// <see cref="NestCard"/>, <see cref="DeviceCard"/> and <see cref="DevicesSummary"/> from the status.
/// </summary>
public partial class DevicesPageView : UserControl
{
    private IReadOnlyList<DeviceCard> _shown = [];
    private IReadOnlyList<PersonRow> _people = [];

    public DevicesPageView()
    {
        InitializeComponent();
        // The nest's four facts 2 by 2, and as many computer cards side by side, as fit (more cut them off).
        NestChips.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                NestChips.Columns = PageLayout.ChipColumns(e.NewSize.Width);
        };
        CardGrid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                CardGrid.Columns = PageLayout.CardColumns(e.NewSize.Width);
        };
        Show(StatusSnapshot.Initial, null, null);
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }


    /// <summary>The computer cards, in order.</summary>
    public IReadOnlyList<DeviceCardView> Cards => CardGrid.Children.OfType<DeviceCardView>().ToList();

    public int CardCount => CardGrid.Children.Count;

    public string EmptyText => DevicesEmpty.Visibility == Visibility.Visible ? DevicesEmptyText.Text : string.Empty;

    public string SummaryText => Summary.Text;

    public string? JoinChipText => JoinChip.Visibility == Visibility.Visible ? JoinText.Text : null;

    public string NestAddressText => NestAddress.Text;

    public string NestVersionText => NestVersion.Text + (NestVersionNote.Text.Length > 0 ? " " + NestVersionNote.Text : string.Empty);

    public string NestStatusText => NestPillText.Text;

    public void Show(StatusSnapshot s, string? thisDevice, string? serverUrl)
    {
        var nest = NestCard.From(s, serverUrl);
        NestHeading.Text = nest.Title;
        NestAddress.Text = nest.Address;
        NestVersion.Text = nest.Version;
        NestVersionNote.Text = nest.VersionNote;
        NestVersionNote.SetResourceReference(TextBlock.ForegroundProperty, nest.VersionOlder ? "T.WarnText" : "T.OkText");
        NestSpace.Text = nest.FreeSpace;
        NestSpace.SetResourceReference(TextBlock.ForegroundProperty, nest.SpaceLow ? "T.WarnText" : "T.Text");
        NestKeeps.Text = nest.Keeps;
        NestPillText.Text = nest.OnlineText;
        NestPill.SetResourceReference(Border.BackgroundProperty, nest.Online ? "T.OkPill" : "T.BadPill");
        NestPillDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, nest.Online ? "T.Ok" : "T.Bad");
        NestPillText.SetResourceReference(TextBlock.ForegroundProperty, nest.Online ? "T.OkText" : "T.BadText");

        var join = DevicesSummary.JoinText(s);
        JoinChip.Visibility = join is null ? Visibility.Collapsed : Visibility.Visible;
        JoinText.Text = join ?? string.Empty;

        if (s.DevicesUnsupported)
            DevicesEmptyText.Text = "Update your nest to see the computers that use it.";
        else if (s.Devices is null)
            DevicesEmptyText.Text = "Loading the computers on your nest…";
        else if (s.Devices.Count == 0)
            DevicesEmptyText.Text = "No computers have used this nest yet.";

        DrawPeople(PeopleSection.From(s));

        var cards = DeviceCard.From(s, thisDevice, DateTimeOffset.UtcNow);
        Summary.Text = DevicesSummary.Line(cards);
        DevicesEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardGrid.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (cards.SequenceEqual(_shown))
            return;
        if (cards.Count == CardGrid.Children.Count)
        {
            // Same computers: update the cards in place (speeds change often).
            for (var i = 0; i < cards.Count; i++)
            {
                if (!Equals(cards[i], _shown.ElementAtOrDefault(i)))
                    ((DeviceCardView)CardGrid.Children[i]).Card = cards[i];
            }
        }
        else
        {
            CardGrid.Children.Clear();
            foreach (var card in cards)
            {
                var view = new DeviceCardView { Card = card, Margin = new Thickness(6) };
                view.MoreRequested += _ => Host?.Actions?.ManageDevices();
                CardGrid.Children.Add(view);
            }
        }
        _shown = cards;
    }

    /// <summary>The People section: shown on the owner's computers of a nest reached through Pairnets (see <see cref="PeopleSection"/>).</summary>
    private void DrawPeople(PeopleSection people)
    {
        PeoplePanel.Visibility = people.Visible ? Visibility.Visible : Visibility.Collapsed;
        PeopleSummary.Text = people.Summary;
        PeopleCard.Visibility = people.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleEmpty.Visibility = people.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!people.Rows.SequenceEqual(_people))
            PeopleList.ItemsSource = people.Rows;
        _people = people.Rows;
    }

    /// <summary>The people shown, in order (a friend, or someone waiting to be let in).</summary>
    public IReadOnlyList<PersonRow> PeopleRows => PeoplePanel.Visibility == Visibility.Visible ? _people : [];

    public string PeopleSummaryText => PeopleSummary.Text;

    private void OnInviteFriend(object sender, RoutedEventArgs e) => Host?.Actions?.InviteFriend();

    // Changing, removing, letting in and declining happen on the account page, at this nest's People.
    private void OnPersonChange(object sender, RoutedEventArgs e) => Host?.Actions?.OpenPeople();

    private void OnPersonRemove(object sender, RoutedEventArgs e) => Host?.Actions?.OpenPeople();

    private void OnPersonDecline(object sender, RoutedEventArgs e) => Host?.Actions?.OpenPeople();

    private void OnPersonLetIn(object sender, RoutedEventArgs e) => Host?.Actions?.OpenPeople();

    private void OnAddComputer(object sender, RoutedEventArgs e) => Host?.Actions?.AddComputer();

    private void OnManageDevices(object sender, RoutedEventArgs e) => Host?.Actions?.ManageDevices();

    private void OnReviewJoin(object sender, RoutedEventArgs e) => Host?.Navigate(MainPage.Attention);
}
