using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>One computer on the Devices page: a card drawn from a <see cref="DeviceCard"/> (its DataContext).</summary>
public partial class DeviceCardView : UserControl
{
    /// <summary>Raised (bubbling) when the card's "…" button is pressed; its source is the card.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> MoreRequestedEvent =
        RoutedEvent.Register<DeviceCardView, RoutedEventArgs>(nameof(MoreRequested), RoutingStrategies.Bubble);

    public DeviceCardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Draw(DataContext as DeviceCard);
    }

    public event EventHandler<RoutedEventArgs> MoreRequested
    {
        add => AddHandler(MoreRequestedEvent, value);
        remove => RemoveHandler(MoreRequestedEvent, value);
    }

    /// <summary>The computer this card shows (for tests).</summary>
    internal DeviceCard? Data => DataContext as DeviceCard;

    /// <summary>The icon slot is a single place: the glyph for <paramref name="icon"/> is chosen here and only here.</summary>
    private void DrawIcon(DeviceIconKind icon) => Glyph.Data = DeviceIconArt.For(icon);

    private void Draw(DeviceCard? card)
    {
        if (card is null)
            return;
        Card.Classes.Set("this", card.IsThis);
        Card.Classes.Set("off", !card.Online);
        Card.Classes.Set("moving", card.Tone == DeviceCardTone.Moving);
        DashRing.IsVisible = !card.Online;
        ThisPill.IsVisible = card.IsThis;
        NameText.Text = card.Name;
        SystemText.Text = card.SystemLine;
        StatusText.Text = card.StatusText;
        NoteText.Text = card.StatusNote.Length > 0 ? "· " + card.StatusNote : string.Empty;
        NoteText.IsVisible = card.StatusNote.Length > 0;
        LastChangeText.Text = card.LastChangeText;
        DrawIcon(card.Icon);
        PencilButton.DeviceId = card.Id;
        PencilButton.DeviceName = card.Name;
        PencilButton.Current = card.Icon;
        PencilButton.IsVisible = card.CanChooseIcon;
        AutomationProperties.SetName(PencilButton, card.IconLabel);
        ToolTip.SetTip(MoreButton, card.MoreLabel);
        AutomationProperties.SetName(MoreButton, card.MoreLabel);
    }

    private void OnMore(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(MoreRequestedEvent, this));
}
