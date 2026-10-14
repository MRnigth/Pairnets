using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// One computer's card on the Devices page, drawn from a <see cref="DeviceCard"/>: its icon, name, system, whether it is
/// online (or when it was last seen) and its last change. The icon is drawn in one place (<see cref="DrawIcon"/>), where the
/// per-computer icon chooser plugs in later; its pencil is built but hidden until then.
/// </summary>
public partial class DeviceCardView : UserControl
{
    /// <summary>Whether the pencil on the icon shows. Off until the icon chooser exists.</summary>
    public const bool PencilEnabled = false;

    private DeviceCard? _card;

    public DeviceCardView()
    {
        InitializeComponent();
        PencilButton.Visibility = PencilEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"More" was pressed (the page opens the nest's Devices page).</summary>
    public event Action<DeviceCard>? MoreRequested;

    public DeviceCard? Card
    {
        get => _card;
        set
        {
            _card = value;
            if (value is not null)
                Draw(value);
        }
    }

    /// <summary>The geometry key of each kind of computer icon.</summary>
    public static string GlyphFor(DeviceIconKind kind) => kind switch
    {
        DeviceIconKind.Laptop => "I.Laptop",
        DeviceIconKind.Office => "I.Office",
        DeviceIconKind.Mini => "I.MiniPc",
        DeviceIconKind.Tv => "I.Tv",
        _ => "I.Desktop",
    };

    /// <summary>The one place that draws a computer's icon (from <see cref="DeviceCard.Icon"/>).</summary>
    private void DrawIcon(DeviceCard card)
    {
        IconSlot.Data = Visuals.Resource<Geometry>(GlyphFor(card.Icon));
        IconSlot.SetResourceReference(LineIcon.StrokeProperty, card.Online ? "T.Text" : "T.TextSoft");
    }

    public string NameLabel => NameText.Text;

    public string StatusLine => StatusText.Text + (NoteText.Text.Length > 0 ? " " + NoteText.Text : string.Empty);

    public string LastChangeLine => LastChangeText.Text;

    public bool ShowsThisPill => ThisPill.Visibility == Visibility.Visible;

    public bool PencilShows => PencilButton.IsVisible;

    private void Draw(DeviceCard card)
    {
        var online = card.Online;
        NameText.Text = card.Name;
        NameText.SetResourceReference(TextBlock.ForegroundProperty, online ? "T.Text" : "T.Muted");
        SystemText.Text = card.SystemLine;
        IconSolid.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        IconDashed.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        DrawIcon(card);
        Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, online ? "T.Ok" : "T.Off");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, online ? "T.Ok" : "T.Off");

        StatusText.Text = card.StatusText;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, online ? "T.OkText" : "T.Muted");
        StatusText.FontWeight = online ? FontWeights.Bold : FontWeights.SemiBold;
        NoteText.Text = card.StatusNote.Length == 0 ? string.Empty : card.Tone == DeviceCardTone.Moving ? card.StatusNote : "· " + card.StatusNote;
        NoteText.SetResourceReference(TextBlock.ForegroundProperty, card.Tone == DeviceCardTone.Moving ? "T.AccentInk" : "T.TextSoft");
        NoteText.FontWeight = card.Tone == DeviceCardTone.Moving ? FontWeights.Bold : FontWeights.Medium;
        NoteText.Visibility = NoteText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ThisPill.Visibility = card.IsThis ? Visibility.Visible : Visibility.Collapsed;
        LastChangeText.Text = card.LastChangeText;

        Frame.SetResourceReference(Border.BorderBrushProperty, card.IsThis ? "T.AccentRing" : "T.Border");
        PencilButton.ToolTip = card.IconLabel;
        System.Windows.Automation.AutomationProperties.SetName(PencilButton, card.IconLabel);
        CardMoreButton.ToolTip = card.MoreLabel;
        System.Windows.Automation.AutomationProperties.SetName(CardMoreButton, card.MoreLabel);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_card is not null)
            MoreRequested?.Invoke(_card);
    }
}
