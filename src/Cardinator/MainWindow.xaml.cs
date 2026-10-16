using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;
using Microsoft.Win32;

namespace Cardinator;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly TemplateService _templates = new();
    private readonly SymbolService _symbols = new();
    private readonly CardRenderer _renderer;
    private readonly ScryfallClient _scryfall = new();
    private readonly DispatcherTimer _renderTimer;

    private CardModel? _selectedCard;
    private Template? _selectedTemplate;
    private BitmapSource? _previewImage;
    private string _status = "";
    private const string Untitled = "Untitled Project";
    private string _projectName = Untitled;
    /// <summary>The name a designed card back prints when its words are left blank: the set's — none while the set has
    /// never been named, so its backs don't print "UNTITLED PROJECT".</summary>
    private string BackSetName => _projectPath.Length == 0 && _projectName == Untitled ? "" : _projectName;
    private string _artBaseDir = "";
    private string _projectPath = "";     // last saved/opened file, so Ctrl+S can re-save silently
    private string? _projectFolder;       // the set folder (holds the project file + art/ + out/); null until saved/opened
    private string? _lastExportDir;       // where the last PNG/sheet went, so "Open output folder" opens there
    private string _defaultTemplate = ""; // the set's saved house frame, inherited by new/imported cards
    private Dictionary<string, System.Text.Json.JsonElement>? _projectUnknown;   // the set's fields from a newer Cardinator
    private SetProfile _setProfile = new();  // the set's shared metadata defaults (W1), inherited by new/imported cards
    private bool _viewingBack;                // DFC: preview is showing the back face (preview-only flip)
    private bool _viewingFlipped;             // flip card: preview is turned upside down to read the other half
    private bool _readingSideways;            // split card: preview is turned a quarter clockwise to read the halves
    private bool _viewingMelded;              // meld card: the preview shows the whole melded card, not this back's half
    private CardModel? _meldPartner;          // meld card: the card in the set whose back prints the melded card's other half
    private bool _activeHalf;                 // split card: art/pan/zoom go to the other half (picked by clicking it)
    private CardModel? _subscribedBack;       // the back face whose edits we're currently tracking
    private CardModel? _subscribedHalf;       // a flip card's other half, tracked the same way
    private bool _isExporting;
    private bool _busy;                     // any long/mutating op in flight (gates re-entrancy + conflicts)
    private double _exportProgress;
    private bool _dirty;
    private bool _loading;                 // suppress dirty-tracking while populating a project

    // Undo/redo: full-project snapshots (serialized cards + selected index). A debounced commit coalesces
    // rapid edits into one step; discrete changes (add/delete/import/lookup/art) are captured too because
    // they trip either the card's PropertyChanged or the Cards collection change.
    private readonly List<(string json, int sel)> _history = new();
    private int _histIdx = -1;
    private int _savedHistIdx;             // history index that matches what's on disk (for accurate dirty state)
    private bool _metaDirty;               // set-level edits the card history can't see (Set defaults, art folder)
    private DispatcherTimer _undoTimer = null!;
    private DispatcherTimer _recoveryTimer = null!;
    private string? _recoveryWritten;      // the recovery copy THIS session wrote (see RecoveryStore)
    private bool _recoveryIsRecovered;     // …or the crashed session's copy it opened: let go, it's kept aside
    private bool _restoring;
    private bool _syncingTemplates;   // RefreshTemplates re-selecting in the picker: never re-frames the card
    private static readonly System.Text.Json.JsonSerializerOptions SnapOpts =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public MainWindow()
    {
        _renderer = new CardRenderer(_symbols);
        InitializeComponent();
        DataContext = this;
        ContentRendered += (_, _) => TellAboutReplacedFrames();
        // The lists go in before the first card is selected: filling one later picks an item, and that change
        // would land on the card as an edit (a fresh window opened with a corner mark of "none" and an undo step).
        LayoutBox.ItemsSource = LayoutOptions;
        DfcStyleBox.ItemsSource = DfcStyleOptions;
        MeldHalfBox.ItemsSource = MeldHalfOptions;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderPreview(); };

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); CommitHistory(); };

        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _recoveryTimer.Tick += (_, _) => WriteRecovery();
        _recoveryTimer.Start();

        Templates = new(_templates.LoadAll());
        _selectedTemplate = Templates.FirstOrDefault();

        Cards = new();
        Cards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(HasCards));
            OnPropertyChanged(nameof(IsEmptySet));
            OnPropertyChanged(nameof(CanExport));
            MarkDirty();
            QueueUndoCommit();
        };

        _loading = true;
        var first = SampleCards.All(DefaultTemplateName).First().Clone();
        Cards.Add(first);
        SelectedCard = first;
        _loading = false;
        Dirty = false;
        SeedHistory();

        RefreshCardControls();

        // Download authentic Scryfall symbols in the background; re-render as they arrive.
        _symbols.Updated += OnSymbolsUpdated;
        _ = _symbols.PrimeAsync();
    }

    // --- the card's layout, and which side the preview shows -----------------

    /// <summary>What kind of card the selected card is — the one Card layout dropdown in place of the separate
    /// double-faced / meld / flip / split buttons. Stored as before (a back face, a meld half, another half with its
    /// layout, adventure fields), so nothing about the file format changes.</summary>
    public enum CardLayout { Normal, DoubleFaced, Meld, Flip, Split, Adventure }

    private sealed record LayoutOption(string Label, CardLayout Value) { public override string ToString() => Label; }
    private static readonly LayoutOption[] LayoutOptions =
    {
        new("Normal", CardLayout.Normal),
        new("Double-faced (a back face)", CardLayout.DoubleFaced),
        new("Meld (half of a bigger card)", CardLayout.Meld),
        new("Flip (a half upside down)", CardLayout.Flip),
        new("Split (two halves side by side)", CardLayout.Split),
        new("Adventure (a creature and a spell)", CardLayout.Adventure),
    };

    /// <summary>The layout the dropdown shows.</summary>
    internal CardLayout? ShownLayout => (LayoutBox.SelectedItem as LayoutOption)?.Value;

    internal static CardLayout LayoutOf(CardModel card) =>
        card.IsMeld ? CardLayout.Meld
        : card.IsDoubleFaced ? CardLayout.DoubleFaced
        : card.IsFlip ? CardLayout.Flip
        : card.IsSplit ? CardLayout.Split
        : card.IsAdventure ? CardLayout.Adventure
        : CardLayout.Normal;

    private static string LayoutName(CardLayout l) => l switch
    {
        CardLayout.DoubleFaced => "double-faced",
        CardLayout.Meld => "meld",
        CardLayout.Flip => "flip",
        CardLayout.Split => "split",
        CardLayout.Adventure => "adventure",
        _ => "normal",
    };

    /// <summary>Display label ↔ stored DfcStyle value for the corner mark dropdown.</summary>
    private sealed record DfcStyleOption(string Label, string Value) { public override string ToString() => Label; }
    private static readonly DfcStyleOption[] DfcStyleOptions =
    {
        new("None", "none"),
        new("Flip arrow", "arrow"),
        new("Sun / moon", "sunmoon"),
        new("Meld", "meld"),
    };

    private static readonly string[] MeldHalfOptions = { "top half", "bottom half" };

    private bool _syncingControls;

    /// <summary>The preview shows the set's card back: Back is picked on a card with no back face of its own.</summary>
    private bool _viewingCardBack;

    /// <summary>The side (or half) the editor column edits: the shown face — its back face on Back, the other half of a
    /// flip or split card once that half is picked — or null while the preview shows the set's card back.</summary>
    public CardModel? EditedFace
    {
        get
        {
            var c = _selectedCard;
            if (c == null || _viewingCardBack) return null;
            if (_viewingBack && c.BackFace != null) return c.BackFace;
            if (c.OtherHalf != null && ((c.IsFlip && _viewingFlipped) || (c.IsSplit && _activeHalf))) return c.OtherHalf;
            return c;
        }
    }

    /// <summary>Syncs everything that shows the selected card's layout and the shown side: the layout dropdown and
    /// its one row of options, the Front | Back switch, the half switch, the editor's heading.</summary>
    private void RefreshCardControls()
    {
        _syncingControls = true;
        try
        {
            var card = _selectedCard;
            var layout = card == null ? CardLayout.Normal : LayoutOf(card);
            LayoutBox.SelectedItem = LayoutOptions.First(o => o.Value == layout);

            DfcOptions.Visibility = layout == CardLayout.DoubleFaced ? Visibility.Visible : Visibility.Collapsed;
            var style = (card?.DfcStyle ?? "").Trim().ToLowerInvariant();
            if (style.Length == 0) style = "none";
            DfcStyleBox.SelectedItem = DfcStyleOptions.FirstOrDefault(o => o.Value == style) ?? DfcStyleOptions[0];

            MeldOptions.Visibility = layout == CardLayout.Meld ? Visibility.Visible : Visibility.Collapsed;
            MeldHalfBox.SelectedItem = card?.IsMeldBottom == true ? MeldHalfOptions[1] : MeldHalfOptions[0];
            MeldPartnerBtn.IsEnabled = _meldPartner == null;
            MeldPartnerBtn.Content = _meldPartner != null ? "Partner ✓" : "Add partner";
            MeldPartnerBtn.ToolTip = _meldPartner != null
                ? $"“{_meldPartner.Name}” is in the set: its back prints the other half"
                : "Add the card that melds with this one, right after it: its back prints the other half";

            RefreshHalfFrameBox();

            LayoutHint.Text = layout switch
            {
                CardLayout.DoubleFaced => "Pick Back above the preview to write the back face.",
                CardLayout.Meld => "The Back is the melded card — write it there; its partner shows it too.",
                CardLayout.Flip => "Pick “Flipped half” above the preview to write the upside-down half.",
                CardLayout.Split => "Pick a half above the preview, or click it, to write it and give it art."
                                    + " Start the second half's rules with “Aftermath” for an aftermath card.",
                CardLayout.Adventure => "Write the adventure — its name, cost, type and rules — in Edit details….",
                _ => "",
            };
            LayoutHint.Visibility = LayoutHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool back = _viewingBack || _viewingCardBack;
            FrontSeg.IsChecked = !back;
            BackSeg.IsChecked = back;
            BackSeg.ToolTip = layout switch
            {
                CardLayout.DoubleFaced => "Show and edit the back face",
                CardLayout.Meld => "Show and edit the melded card: this card's back prints one half of it",
                _ => "Show the back: the set's card back, which every card without a back face shares",
            };

            bool halves = !back && layout is CardLayout.Flip or CardLayout.Split && card != null;
            HalfBar.Visibility = halves ? Visibility.Visible : Visibility.Collapsed;
            FirstHalfSeg.Content = layout == CardLayout.Flip ? "Top half" : "First half";
            OtherHalfSeg.Content = layout == CardLayout.Flip ? "Flipped half" : "Second half";
            bool other = layout == CardLayout.Flip ? _viewingFlipped : _activeHalf;
            FirstHalfSeg.IsChecked = !other;
            OtherHalfSeg.IsChecked = other;
            SidewaysBox.Visibility = halves && layout == CardLayout.Split ? Visibility.Visible : Visibility.Collapsed;
            SidewaysBox.IsChecked = _readingSideways;
            WholeMeldBox.Visibility = layout == CardLayout.Meld && _viewingBack ? Visibility.Visible : Visibility.Collapsed;
            WholeMeldBox.IsChecked = _viewingMelded;

            CardBackNote.Visibility = _viewingCardBack ? Visibility.Visible : Visibility.Collapsed;
            FaceEditor.Visibility = _viewingCardBack ? Visibility.Collapsed : Visibility.Visible;
            FaceFooter.Visibility = FaceEditor.Visibility;   // the checks and Export are about a face
            CardExportBar.Visibility = FaceEditor.Visibility;
            var face = EditedFace;
            NameLabel.Text = card == null || face == null || ReferenceEquals(face, card) ? "CARD NAME"
                : ReferenceEquals(face, card.BackFace) ? (layout == CardLayout.Meld ? "MELDED CARD NAME" : "BACK FACE NAME")
                : layout == CardLayout.Flip ? "FLIPPED HALF NAME" : "SECOND HALF NAME";
            OnPropertyChanged(nameof(EditedFace));
        }
        finally { _syncingControls = false; }
    }

    private void OnLayoutChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null || LayoutBox.SelectedItem is not LayoutOption pick) return;
        if (!ChangeLayout(_selectedCard, pick.Value)) RefreshCardControls();   // cancelled: show the layout it still has
    }

    /// <summary>On the closed layout dropdown the arrow keys would change the card's layout at every press (each a real
    /// change, some asking to throw a face away, and a cancel leaving the card on the layout before): open the list
    /// instead, where the arrows only move the highlight and Enter picks.</summary>
    private void OnLayoutKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (LayoutBox.IsDropDownOpen) return;
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or System.Windows.Input.Key.Home
            or System.Windows.Input.Key.End or System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
        {
            LayoutBox.IsDropDownOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>Something on a face worth asking about before it's thrown away.</summary>
    private static bool FaceHasContent(CardModel? face) => face != null &&
        (face.Name.Trim().Length > 0 || face.RulesText.Trim().Length > 0 || face.TypeLine.Trim().Length > 0
         || face.ManaCost.Trim().Length > 0 || face.ArtPath.Trim().Length > 0 || face.FlavorText.Trim().Length > 0
         || face.Power.Trim().Length > 0 || face.Toughness.Trim().Length > 0 || face.Loyalty.Trim().Length > 0
         || face.Defense.Trim().Length > 0);

    /// <summary>Turns <paramref name="card"/> into a card of another layout, as one undo step. What both layouts use
    /// is kept — the back face between double-faced and meld, the other half between flip and split — and anything
    /// else that would be lost is asked about first (Undo brings it back). Returns false when the user cancels.</summary>
    internal bool ChangeLayout(CardModel card, CardLayout target)
    {
        var from = LayoutOf(card);
        if (from == target) return true;
        bool keepBack = from is CardLayout.DoubleFaced or CardLayout.Meld && target is CardLayout.DoubleFaced or CardLayout.Meld;
        bool keepHalf = from is CardLayout.Flip or CardLayout.Split && target is CardLayout.Flip or CardLayout.Split;

        var lost = new List<string>();
        if (card.BackFace != null && !keepBack && FaceHasContent(card.BackFace))
            lost.Add(from == CardLayout.Meld ? $"its melded card “{card.BackFace.Name}”" : $"its back face “{card.BackFace.Name}”");
        if (card.OtherHalf != null && !keepHalf && FaceHasContent(card.OtherHalf))
            lost.Add($"its other half “{card.OtherHalf.Name}”");
        if (from == CardLayout.Adventure && target != CardLayout.Adventure
            && (card.AdventureName + card.AdventureText + card.AdventureCost).Trim().Length > 0)
            lost.Add($"its adventure “{card.AdventureName}”");
        if (lost.Count > 0 && ConfirmDialog.Show(this, "Change the layout",
                $"Make this a {LayoutName(target)} card? It loses {string.Join(" and ", lost)}. Undo (Ctrl+Z) brings it back.",
                affirmative: "Change", cancel: "Cancel") != ConfirmResult.Affirmative)
            return false;

        // Take off what the new layout doesn't use.
        if (!keepBack && card.BackFace != null)
        {
            card.BackFace = null;
            card.MeldHalf = "";
            _meldPartner = null;
        }
        if (!keepHalf && card.OtherHalf != null)
        {
            card.OtherHalf = null;
            card.HalfLayout = "";
            card.HalfTemplateName = "";
        }
        if (from == CardLayout.Adventure)
        {
            card.AdventureName = card.AdventureCost = card.AdventureType = card.AdventureText = "";
            if (string.Equals(card.Layout, "adventure", StringComparison.OrdinalIgnoreCase)) card.Layout = "";
        }
        if (target is not (CardLayout.DoubleFaced or CardLayout.Meld) && card.DfcStyle == "meld") card.DfcStyle = "";

        // Put on what it needs.
        string status;
        switch (target)
        {
            case CardLayout.DoubleFaced:
                EnsureBackFace(card);
                card.MeldHalf = "";
                if (string.IsNullOrWhiteSpace(card.DfcStyle) || card.DfcStyle is "meld" or "none") card.DfcStyle = "sunmoon";
                status = "It's double-faced now — pick Back above the preview to write the back face.";
                break;
            case CardLayout.Meld:
                EnsureBackFace(card);
                if (!card.IsMeld) card.MeldHalf = "top";
                if (string.IsNullOrWhiteSpace(card.DfcStyle) || card.DfcStyle is "none" or "sunmoon" or "arrow") card.DfcStyle = "meld";
                status = "It's a meld card now — its Back is the melded card, and prints its top half. "
                         + "“Add partner” adds the card that prints the other half.";
                break;
            case CardLayout.Flip:
            case CardLayout.Split:
                card.OtherHalf ??= new CardModel { TemplateName = card.TemplateName };
                card.HalfLayout = target == CardLayout.Flip ? "flip" : "split";
                status = target == CardLayout.Flip
                    ? "It's a flip card now — pick “Flipped half” above the preview to write the upside-down half."
                    : "It's a split card now — pick “Second half” above the preview (or click it) to write it and give it art.";
                break;
            case CardLayout.Adventure:
                card.Layout = "adventure";
                if (card.AdventureType.Trim().Length == 0) card.AdventureType = "Sorcery — Adventure";
                status = "It has an adventure now — write it in Edit details….";
                break;
            default:
                status = "It's a normal card now.";
                break;
        }

        // Only a meld card has a partner: a meld card made double-faced keeps its back but must stop copying its edits
        // onto the old partner's.
        _meldPartner = target == CardLayout.Meld ? MeldPartner(card) : null;
        _viewingBack = _viewingMelded = _viewingFlipped = _readingSideways = _activeHalf = _viewingCardBack = false;
        MarkDirty();
        CommitHistory();
        SyncFramePicker();
        RefreshCardControls();
        RenderPreview();
        Status = status;
        return true;
    }

    /// <summary>Gives a card a blank back face, on its frame and with the set's details, unless it has one.</summary>
    private void EnsureBackFace(CardModel card)
    {
        if (card.BackFace != null) return;
        var back = new CardModel { TemplateName = card.TemplateName };
        _setProfile.ApplyDefaults(back);
        card.BackFace = back;
    }

    private void OnSideChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null) return;
        ShowSide(back: BackSeg.IsChecked == true);
    }

    /// <summary>Shows the front, or the back: the card's back face, or — when it has none — the set's card back.</summary>
    internal void ShowSide(bool back)
    {
        var card = _selectedCard;
        if (card == null) return;
        if (back)
        {
            _viewingBack = card.BackFace != null;
            _viewingCardBack = card.BackFace == null;
            _viewingFlipped = _readingSideways = _activeHalf = false;
        }
        else
        {
            _viewingBack = _viewingMelded = _viewingCardBack = false;
        }
        SyncFramePicker();
        RefreshCardControls();
        RenderPreview();
    }

    private void OnHalfChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null) return;
        ShowHalf(other: OtherHalfSeg.IsChecked == true);
    }

    /// <summary>Flip or split card: shows and edits the other half (a flip card's turns upside down to read it), or the
    /// first.</summary>
    internal void ShowHalf(bool other)
    {
        var card = _selectedCard;
        if (card?.OtherHalf == null) return;
        if (card.IsFlip) _viewingFlipped = other;
        else if (card.IsSplit)
        {
            _activeHalf = other;
            Status = $"Art now goes to “{ActiveFace?.Name}” — drag to move its art, scroll to zoom.";
        }
        RefreshCardControls();
        RenderPreview();
    }

    private void OnSidewaysChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null) return;
        if (!_selectedCard.IsSplit || _viewingBack || _viewingCardBack) { RefreshCardControls(); return; }   // only shown on a split card's front
        _readingSideways = SidewaysBox.IsChecked == true;
        RenderPreview();
    }

    private void OnWholeMeldChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null) return;
        if (!_selectedCard.IsMeld || !_viewingBack) { RefreshCardControls(); return; }   // only shown on a meld card's back
        _viewingMelded = WholeMeldBox.IsChecked == true;
        RenderPreview();
    }

    private void OnChangeCardBack(object sender, RoutedEventArgs e) => OnSetSettings(sender, e);

    private void OnNewCardMenu(object sender, RoutedEventArgs e) => OpenMenu(NewCardBtn);
    private void OnFrameMenu(object sender, RoutedEventArgs e) => OpenMenu(FrameMenuBtn);

    private static void OpenMenu(System.Windows.Controls.Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnDfcStyleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingControls || _selectedCard == null) return;
        if (DfcStyleBox.SelectedItem is DfcStyleOption opt && _selectedCard.DfcStyle != opt.Value)
        {
            _selectedCard.DfcStyle = opt.Value;   // setter also syncs the back face
            MarkDirty();
            QueueUndoCommit();
            RenderPreview();
        }
    }

    // --- meld (two cards whose backs make one big card) --------------------------

    /// <summary>The card in the set whose back prints the other half of <paramref name="card"/>'s melded card: another
    /// meld card with the opposite half and the same melded card (or named as its partner), or null.</summary>
    internal CardModel? MeldPartner(CardModel card)
    {
        if (!card.IsMeld) return null;
        if (PairedByMeldedName(card) is { } byName) return byName;
        // Named as its partner — unless that card's melded card already pairs it with another.
        if (card.MeldWith.Trim().Length == 0) return null;
        return Cards.FirstOrDefault(c => !ReferenceEquals(c, card) && c.IsMeld && c.IsMeldBottom != card.IsMeldBottom
            && string.Equals(c.Name.Trim(), card.MeldWith.Trim(), StringComparison.OrdinalIgnoreCase)
            && PairedByMeldedName(c) is var taken && (taken == null || ReferenceEquals(taken, card)));
    }

    /// <summary>The partner whose back names the same melded card. Several pairs can share a melded card (a duplicated
    /// meld card), so the cards pair up in list order: the second top half with that melded card goes with the second
    /// bottom half. A blank melded card pairs with nothing — two new meld cards aren't each other's halves just because
    /// neither has a name yet.</summary>
    private CardModel? PairedByMeldedName(CardModel card)
    {
        var melded = card.BackFace!.Name.Trim();
        if (melded.Length == 0) return null;
        bool Same(CardModel c) => c.IsMeld && string.Equals(c.BackFace!.Name.Trim(), melded, StringComparison.OrdinalIgnoreCase);
        int rank = Cards.Where(c => Same(c) && c.IsMeldBottom == card.IsMeldBottom).TakeWhile(c => !ReferenceEquals(c, card)).Count();
        return Cards.Where(c => Same(c) && c.IsMeldBottom != card.IsMeldBottom).Skip(rank).FirstOrDefault();
    }

    /// <summary>Copies one changed property of the selected meld card's back (the melded card) onto its partner's
    /// back, so both halves always show the same melded card — whatever edited it (details, art, pan, zoom).</summary>
    private void SyncMeldBack(string? property)
    {
        if (_selectedCard?.BackFace is not { } back || _meldPartner?.BackFace is not { } other || property == null) return;
        if (property is nameof(CardModel.MeldHalf) or nameof(CardModel.IsBackFace) or nameof(CardModel.BackFace)
            or nameof(CardModel.OtherHalf) or nameof(CardModel.DfcStyle)) return;
        var p = typeof(CardModel).GetProperty(property);
        if (p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
            p.SetValue(other, p.GetValue(back));
    }

    private void OnAddMeldPartner(object sender, RoutedEventArgs e) => AddMeldPartner();

    internal void AddMeldPartner()
    {
        var card = _selectedCard;
        if (card?.IsMeld != true || MeldPartner(card) != null) return;
        // The two halves find each other by their melded card's name, and a blank one pairs with nothing: give it a
        // name no other card's melded card has (shown on the back, to be typed over).
        if (card.BackFace!.Name.Trim().Length == 0)
            card.BackFace.Name = Enumerable.Range(1, int.MaxValue).Select(i => $"Melded card {i}")
                .First(n => !Cards.Any(c => c.IsMeld && string.Equals(c.BackFace!.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)));
        var partner = new CardModel { Name = card.MeldWith, TemplateName = card.TemplateName, DfcStyle = card.DfcStyle };
        _setProfile.ApplyDefaults(partner);
        partner.BackFace = card.BackFace!.Clone();
        partner.MeldHalf = card.IsMeldBottom ? "top" : "bottom";
        partner.MeldWith = card.Name;
        Cards.Insert(Cards.IndexOf(card) + 1, partner);
        _meldPartner = partner;
        MarkDirty();
        CommitHistory();
        RefreshCardControls();
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(HasCards));
        Status = partner.Name.Length > 0
            ? $"Added “{partner.Name}” after this card — its back prints the {partner.MeldHalf} half. Select it and look it up (Ctrl+L) to fill its front."
            : $"Added the meld partner after this card — its back prints the {partner.MeldHalf} half. Select it to fill in its front.";
    }

    private void OnMeldHalfChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingControls || MeldHalfBox.SelectedItem is not string pick) return;
        SetMeldHalf(bottom: pick == MeldHalfOptions[1]);
    }

    /// <summary>Which half of the melded card this card's back prints; its partner, if any, prints the other.</summary>
    internal void SetMeldHalf(bool bottom)
    {
        var card = _selectedCard;
        if (card?.IsMeld != true || card.IsMeldBottom == bottom) return;
        card.MeldHalf = bottom ? "bottom" : "top";
        if (_meldPartner != null) _meldPartner.MeldHalf = bottom ? "top" : "bottom";
        MarkDirty();
        CommitHistory();
        RefreshCardControls();
        RenderPreview();
        Status = $"This card's back now prints the {card.MeldHalf} half"
                 + (_meldPartner != null ? $"; “{_meldPartner.Name}” prints the other." : ".");
    }

    // --- flip and split cards (two halves, one face) ------------------------

    /// <summary>The first entry of the other half's frame list: no frame of its own.</summary>
    internal const string SameFrameAsCard = "Same as the card";
    private bool _syncingHalfFrame;

    /// <summary>Shows the other half's frame list on a split card, selecting its own frame (or "Same as the card").
    /// A frame it names that isn't installed stays listed, so the choice isn't silently lost.</summary>
    private void RefreshHalfFrameBox()
    {
        var card = _selectedCard;
        bool split = card?.IsSplit == true;
        HalfFramePanel.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        if (!split) return;
        _syncingHalfFrame = true;
        try
        {
            var names = new List<string> { SameFrameAsCard };
            names.AddRange(Templates.Select(t => t.Name));
            var own = card!.HalfTemplateName;
            if (own.Length > 0 && !names.Contains(own)) names.Add(own);
            HalfFrameBox.ItemsSource = names;
            HalfFrameBox.SelectedItem = own.Length > 0 ? own : SameFrameAsCard;
        }
        finally { _syncingHalfFrame = false; }
    }

    private void OnHalfFrameChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingHalfFrame || _selectedCard?.IsSplit != true || HalfFrameBox.SelectedItem is not string pick) return;
        var name = pick == SameFrameAsCard ? "" : pick;
        if (name == _selectedCard.HalfTemplateName) return;
        _selectedCard.HalfTemplateName = name;
        Status = name.Length == 0 ? $"“{_selectedCard.OtherHalf!.Name}” uses the card's frame again."
                                  : $"“{_selectedCard.OtherHalf!.Name}” now has the {name} frame.";
        MarkDirty();
        QueueUndoCommit();
        RenderPreview();
    }

    /// <summary>Turns a drag or nudge on the preview (a fraction of the shown image's width/height) into the
    /// change to the active part's art offset, so the art always moves the way the mouse does: reversed while
    /// a flip card is shown upside down, and for a split/aftermath card turned and scaled into the small half.</summary>
    private (double dx, double dy) PanDelta(double dx, double dy)
    {
        var card = PreviewSide;
        if (card?.IsFlip == true && _viewingFlipped) return (-dx, -dy);
        if (card?.IsMeldBack == true && !_viewingMelded && TemplateFor(card) is { } meldTemplate)
        {
            // This back shows half of the melded card, bigger and turned: move its art the way the mouse goes.
            var part = CardRenderer.MeldPart(card, meldTemplate);
            var v = part.FromCard(new System.Windows.Vector(dx * meldTemplate.Spec.CanvasWidth, dy * meldTemplate.Spec.CanvasHeight));
            return (v.X / part.Width, v.Y / part.Height);
        }
        if (card?.IsSplit == true && TemplateFor(card) is { } template)
        {
            var (toCard, dw, dh) = PreviewToCard(card, template.Spec);
            var part = ActivePart(CardRenderer.SplitGeometry(card, template));
            var v = part.FromCard(toCard.Transform(new System.Windows.Vector(dx * dw, dy * dh)));
            return (v.X / part.Width, v.Y / part.Height);
        }
        return (dx, dy);
    }

    /// <summary>Where the preview image's coordinates (in card units) land on the upright card, and the shown
    /// image's size in card units: identity when upright, a quarter turn back when a split card is being read
    /// sideways (clockwise) or an aftermath card (counter-clockwise).</summary>
    private (System.Windows.Media.Matrix toCard, double w, double h) PreviewToCard(CardModel card, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        if (!(_readingSideways && card.IsSplit)) return (System.Windows.Media.Matrix.Identity, W, H);
        return card.IsAftermath ? (new System.Windows.Media.Matrix(0, 1, -1, 0, W, 0), H, W) : (new System.Windows.Media.Matrix(0, -1, 1, 0, 0, H), H, W);
    }

    private CardRenderer.SplitPart ActivePart(CardRenderer.SplitLayout g) => _activeHalf ? g.Other : g.First;

    /// <summary>Split/aftermath card: which half is under a point on the preview — false = the first half,
    /// true = the other half, null = neither (or not such a card).</summary>
    private bool? SplitHalfAt(System.Windows.Point p)
    {
        var card = PreviewSide;
        if (card?.IsSplit != true || TemplateFor(card) is not { } template) return null;
        var (w, h) = DisplayedCardSize();
        if (w <= 0 || h <= 0) return null;
        double u = (p.X - (PreviewImageControl.ActualWidth - w) / 2) / w;
        double v = (p.Y - (PreviewImageControl.ActualHeight - h) / 2) / h;
        if (u < 0 || u > 1 || v < 0 || v > 1) return null;
        var (toCard, dw, dh) = PreviewToCard(card, template.Spec);
        var at = toCard.Transform(new System.Windows.Point(u * dw, v * dh));
        var g = CardRenderer.SplitGeometry(card, template);
        if (g.First.FromCard(at) != null) return false;
        if (g.Other.FromCard(at) != null) return true;
        return null;
    }

    /// <summary>Split card: makes the half under <paramref name="p"/> the one art/pan/zoom go to, and says so
    /// when that changes.</summary>
    private void PickSplitHalfAt(System.Windows.Point p)
    {
        if (_viewingCardBack) return;   // the set's card back is showing, not the card's halves
        if (SplitHalfAt(p) is not { } other || other == _activeHalf) return;
        ShowHalf(other);
    }

    // --- bindable state -----------------------------------------------------

    public ObservableCollection<Template> Templates { get; }
    public ObservableCollection<CardModel> Cards { get; }

    public CardModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            DetachCardEvents(_selectedCard);
            _selectedCard = value;
            _viewingBack = false;   // always start a newly-selected card on its front face
            _viewingFlipped = false;
            _readingSideways = false;
            _activeHalf = false;
            _viewingMelded = false;
            _viewingCardBack = false;
            _meldPartner = _selectedCard != null ? MeldPartner(_selectedCard) : null;
            if (_selectedCard != null)
            {
                AttachCardEvents(_selectedCard);
                SyncFramePicker();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanEditSelected));
            RefreshCardControls();
            RenderPreview();
        }
    }

    public bool HasSelection => _selectedCard != null;

    /// <summary>The side the preview currently shows — the back when flipped (DFC), otherwise the front.</summary>
    private CardModel? PreviewSide =>
        _viewingBack && _selectedCard?.BackFace != null ? _selectedCard.BackFace : _selectedCard;

    /// <summary>The part that art, pan and zoom go to: the shown side — or, on a split card, the other half
    /// once it has been clicked in the preview.</summary>
    private CardModel? ActiveFace =>
        _viewingCardBack ? null : _activeHalf && PreviewSide is { IsSplit: true } s ? s.OtherHalf : PreviewSide;

    /// <summary>The template a given face ACTUALLY renders with: its own frame (falling back to the current one),
    /// turned to its landscape layout when the face is a Battle/Plane. Resolving here means the CHECKS panel
    /// and the pixel inspector judge the same geometry the preview draws.</summary>
    private Template? TemplateFor(CardModel card)
    {
        var t = BaseTemplateFor(card);
        return t == null ? null : TemplateService.ResolveFor(card, t);
    }

    /// <summary>The installed frame a face is drawn with: its own; for a back face with none, its front's; else (its
    /// frame isn't installed) the set's house frame or the first one — never just whichever frame the picker last
    /// showed, so a card on a missing frame looks the same however you got to it.</summary>
    private Template? BaseTemplateFor(CardModel card)
    {
        if (Templates.FirstOrDefault(x => x.Name == card.TemplateName) is { } own) return own;
        if (string.IsNullOrWhiteSpace(card.TemplateName) && _selectedCard is { } front && ReferenceEquals(front.BackFace, card)
            && Templates.FirstOrDefault(x => x.Name == front.TemplateName) is { } frontFrame)
            return frontFrame;
        return Templates.FirstOrDefault(x => x.Name == _defaultTemplate) ?? Templates.FirstOrDefault();
    }

    /// <summary>Points the frame picker at the shown face's own frame — blank when that frame isn't installed (the
    /// card is flagged for it), so the picker, Design… and Delete never act on a frame the card doesn't use.</summary>
    private void SyncFramePicker()
    {
        if (PreviewSide is not { } face) return;
        _selectedTemplate = string.IsNullOrWhiteSpace(face.TemplateName)
            ? BaseTemplateFor(face)
            : Templates.FirstOrDefault(x => x.Name == face.TemplateName);
        _syncingTemplates = true;
        try { OnPropertyChanged(nameof(SelectedTemplate)); }
        finally { _syncingTemplates = false; }
    }

    /// <summary>Puts a card on a frame, taking along an ordinary back face that was on the same frame (or none) —
    /// a back face made with the card keeps following it; one given a frame of its own keeps that.</summary>
    internal static void SetFrame(CardModel card, string name)
    {
        if (card.BackFace is { } back && !card.IsMeld
            && (string.IsNullOrWhiteSpace(back.TemplateName) || back.TemplateName == card.TemplateName))
            back.TemplateName = name;
        card.TemplateName = name;
    }

    /// <summary>Short app version shown in the header (e.g. "v1.1.4"), so the running build is obvious.</summary>
    public string AppVersion => "v" + App.VersionNumber();

    /// <summary>True when the project has at least one card (gates batch/export actions).</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>No cards at all: the preview shows where to start instead of an empty frame.</summary>
    public bool IsEmptySet => Cards.Count == 0;

    /// <summary>A long/mutating operation (lookup, import, search, export…) is running. While true, other
    /// actions are disabled and re-entrant handlers early-return, so nothing conflicts or double-fires.</summary>
    public bool Busy
    {
        get => _busy;
        internal set   // internal: tests drive the busy state to check the guards
        {
            _busy = value;
            if (!value && _undoPending) { _undoPending = false; QueueUndoCommit(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotBusy));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanEditSelected));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    /// <summary>Convenience inverse of <see cref="Busy"/> for "enabled unless busy" bindings.</summary>
    public bool NotBusy => !_busy;

    /// <summary>The batch/export actions are usable: there are cards and nothing else is running.</summary>
    public bool CanExport => Cards.Count > 0 && !_busy;

    /// <summary>The per-card editor is usable: a card is selected and nothing else is running.</summary>
    public bool CanEditSelected => _selectedCard != null && !_busy;

    public Template? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            _selectedTemplate = value;
            OnPropertyChanged();
            // Only push the frame onto the card for a genuine user change — not during restore/load or a
            // programmatic re-sync (which would spuriously dirty the card / churn undo history). It goes on the
            // face being shown: the back face's own frame while the back is shown.
            if (value != null && PreviewSide is { } face && !_restoring && !_loading && !_syncingTemplates
                && !string.Equals(value.Name, face.TemplateName, StringComparison.Ordinal))
            {
                if (ReferenceEquals(face, _selectedCard)) SetFrame(face, value.Name);
                else face.TemplateName = value.Name;
            }
            RenderPreview();
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set { _previewImage = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool IsExporting
    {
        get => _isExporting;
        set { _isExporting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanExport)); }
    }

    public double ExportProgress
    {
        get => _exportProgress;
        set { _exportProgress = value; OnPropertyChanged(); }
    }

    private bool _progressIndeterminate = true;
    /// <summary>When busy without a known item count (fetch, single lookup), the progress bar animates
    /// instead of showing a percentage — so a long operation reads as "working", never "hung".</summary>
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        set { _progressIndeterminate = value; OnPropertyChanged(); }
    }

    private string _validationSummary = "";
    /// <summary>A one-line health line for the selected card (e.g. "✓ No issues" or "⚠ 1 error · 2 warnings").</summary>
    public string ValidationSummary
    {
        get => _validationSummary;
        private set { _validationSummary = value; OnPropertyChanged(); }
    }

    private string _validationDetails = "";
    /// <summary>The bulleted list of warnings/errors for the selected card (empty when it's clean).</summary>
    public string ValidationDetails
    {
        get => _validationDetails;
        private set { _validationDetails = value; OnPropertyChanged(); }
    }

    private bool _hasValidationIssues;
    /// <summary>Whether to show the detail list (there's at least one warning or error).</summary>
    public bool HasValidationIssues
    {
        get => _hasValidationIssues;
        private set { _hasValidationIssues = value; OnPropertyChanged(); }
    }

    public string ProjectSummary => $"{_projectName} — {Cards.Count} {(Cards.Count == 1 ? "card" : "cards")}";

    public string WindowTitle => (_dirty ? "● " : "") + _projectName + " — Cardinator " + AppVersion;

    /// <summary>Tracks unsaved edits so we can warn before losing work and mark the title bar.</summary>
    public bool Dirty
    {
        get => _dirty;
        internal set { _dirty = value; OnPropertyChanged(nameof(WindowTitle)); }   // internal: tests set a saved baseline
    }

    private void MarkDirty() { if (!_loading) Dirty = true; }

    // New/imported cards inherit the set's saved house frame when it's still installed; otherwise fall back to
    // the current selection, then the first installed frame.
    private string DefaultTemplateName =>
        (!string.IsNullOrWhiteSpace(_defaultTemplate) && Templates.Any(t => t.Name == _defaultTemplate) ? _defaultTemplate : null)
        ?? _selectedTemplate?.Name ?? Templates.FirstOrDefault()?.Name ?? "";

    // --- rendering ----------------------------------------------------------

    /// <summary>Background symbol-prime finished — re-render, unless the window/dispatcher is shutting down
    /// (a late callback from an in-flight prime must not throw on a dead dispatcher).</summary>
    private void OnSymbolsUpdated()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _renderer.ClearSymbolCaches();   // re-derive pip-sampled colors from the real symbol art
        try { Dispatcher.BeginInvoke(new Action(RenderPreview)); } catch { /* shutting down */ }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A back face added/removed/replaced means a different object to track from now on.
        if (e.PropertyName == nameof(CardModel.BackFace)) SyncBackFaceSubscription(_selectedCard);
        if (e.PropertyName == nameof(CardModel.OtherHalf)) SyncOtherHalfSubscription(_selectedCard);
        if (ReferenceEquals(sender, _subscribedBack) && !_restoring) SyncMeldBack(e.PropertyName);
        if (e.PropertyName is not (nameof(CardModel.ArtOffsetX) or nameof(CardModel.ArtOffsetY) or nameof(CardModel.ArtScale)))
            _artDrag = null;   // the live drag's overlay shows the card as it was
        MarkDirty();
        _renderTimer.Stop();
        if (!_isPanning) _renderTimer.Start();   // debounce rapid edits (typing, slider drags); a drag renders on release
        QueueUndoCommit();
    }

    /// <summary>Starts tracking a card's edits — AND its back face's. A back face is a separate
    /// <see cref="CardModel"/>, so without this every edit made on the flipped preview (pan, zoom, paste,
    /// clear art) would skip MarkDirty/undo/re-render and be silently lost on close.</summary>
    private void AttachCardEvents(CardModel? card)
    {
        if (card == null) return;
        card.PropertyChanged += OnCardChanged;
        SyncBackFaceSubscription(card);
        SyncOtherHalfSubscription(card);
    }

    private void DetachCardEvents(CardModel? card)
    {
        if (card != null) card.PropertyChanged -= OnCardChanged;
        SyncBackFaceSubscription(null);
        SyncOtherHalfSubscription(null);
    }

    /// <summary>Points the back-face subscription at <paramref name="card"/>'s current back face (if any),
    /// detaching whichever one we were tracking before. Idempotent, so it can't double-subscribe.</summary>
    private void SyncBackFaceSubscription(CardModel? card)
    {
        var back = card?.BackFace;
        if (ReferenceEquals(back, _subscribedBack)) return;
        if (_subscribedBack != null) _subscribedBack.PropertyChanged -= OnCardChanged;
        _subscribedBack = back;
        if (_subscribedBack != null) _subscribedBack.PropertyChanged += OnCardChanged;
    }

    /// <summary>Same as <see cref="SyncBackFaceSubscription"/>, for a flip card's other half — edits made to
    /// it (in its details dialog) must dirty the project and reach undo like any other.</summary>
    private void SyncOtherHalfSubscription(CardModel? card)
    {
        var half = card?.OtherHalf;
        if (ReferenceEquals(half, _subscribedHalf)) return;
        if (_subscribedHalf != null) _subscribedHalf.PropertyChanged -= OnCardChanged;
        _subscribedHalf = half;
        if (_subscribedHalf != null) _subscribedHalf.PropertyChanged += OnCardChanged;
    }

    // --- undo / redo --------------------------------------------------------

    /// <summary>Whether there's a prior state to step back to.</summary>
    public bool CanUndo => _histIdx > 0 && !_busy;
    /// <summary>Whether there's a forward state to redo.</summary>
    public bool CanRedo => _histIdx >= 0 && _histIdx < _history.Count - 1 && !_busy;

    private void SeedHistory()
    {
        _history.Clear();
        _history.Add((SnapshotJson(), SelectedIndex()));
        _histIdx = 0;
        _savedHistIdx = 0;   // a freshly seeded project matches its on-disk (or blank) baseline
        _metaDirty = false;
        UpdateUndoRedo();
        ClearRecovery();     // a different set now: the last one was saved or let go
    }

    /// <summary>Marks the current history position as the saved baseline (called after Save/Open).</summary>
    private void MarkSavedPoint()
    {
        _savedHistIdx = _histIdx;
        _metaDirty = false;
        Dirty = false;
        ClearRecovery(saved: true);
    }

    // --- recovery copies (unsaved work that never reaches a Save) --------------

    /// <summary>Writes a recovery copy of the set while it has unsaved changes: every minute, when Windows ends the
    /// session (no Closing event, so no save prompt), and when the app hits an error. Never throws.</summary>
    internal void WriteRecovery()
    {
        if (SuppressClosePrompt || !_dirty || _loading || _restoring) return;
        try
        {
            var file = RecoveryStore.FileFor(_projectPath);
            if (_recoveryWritten != null && _recoveryWritten != file) ClearRecovery();
            RecoveryStore.Write(file, _projectPath, _projectName, Cards, _artBaseDir, _defaultTemplate, _setProfile);
            _recoveryWritten = file;
        }
        catch { /* a recovery copy must never get in the way */ }
    }

    /// <summary>Removes this session's recovery copy (the set was saved, or the user chose not to keep the changes).
    /// A crashed session's copy that was opened is kept aside in <c>recovery\declined</c> instead, unless it was saved:
    /// "Don't save" by mistake must not be the end of that work, just as "Discard" at startup isn't.</summary>
    internal void ClearRecovery(bool saved = false)
    {
        if (_recoveryWritten == null) return;
        if (_recoveryIsRecovered && !saved) RecoveryStore.Decline(_recoveryWritten);
        else RecoveryStore.Delete(_recoveryWritten);
        _recoveryWritten = null;
        _recoveryIsRecovered = false;
    }

    /// <summary>At startup: offers the newest recovery copy a previous session left behind.</summary>
    internal void OfferRecovery()
    {
        var pending = RecoveryStore.Pending();
        if (pending.Count == 0) return;
        var entry = pending[0];
        var which = string.IsNullOrWhiteSpace(entry.Meta.SetPath)
            ? $"a set that was never saved (“{entry.Meta.Name}”)" : $"“{Path.GetFileName(entry.Meta.SetPath)}”";
        var choice = ConfirmDialog.Show(this, "Recover unsaved changes",
            $"Cardinator closed at {entry.Meta.SavedAt:g} with unsaved changes to {which}.\n\n"
            + "Open the recovered copy? Save it to keep it."
            + (SetSavedSince(entry) is { } saved
                ? $"\n\nCareful: the set was saved again after that, at {saved:g}. This copy is OLDER than the set — "
                  + "saving it would replace the newer set (which would stay in its backups)." : "")
            + (pending.Count > 1 ? $"\n\n({pending.Count - 1} more will be offered next time.)" : ""),
            affirmative: "Open it", negative: "Discard", cancel: "Later");
        if (choice == ConfirmResult.Affirmative) OpenRecovered(entry);
        else if (choice == ConfirmResult.Negative) RecoveryStore.Decline(entry.File);
    }

    /// <summary>When the set a recovery copy belongs to was saved after the copy was made (its work was redone and
    /// saved since), or null.</summary>
    internal static DateTime? SetSavedSince(RecoveryStore.Entry entry)
    {
        try
        {
            var path = entry.Meta.SetPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var written = File.GetLastWriteTime(path);
            var made = entry.Meta.SavedAt.Kind == DateTimeKind.Utc ? entry.Meta.SavedAt.ToLocalTime() : entry.Meta.SavedAt;
            return written > made.AddSeconds(2) ? written : null;
        }
        catch { return null; }
    }

    /// <summary>Loads a recovery copy as the set it belongs to: unsaved, and Save goes to that set's file.</summary>
    internal bool OpenRecovered(RecoveryStore.Entry entry)
    {
        if (!LoadProjectFile(entry.File)) return false;
        var setPath = entry.Meta.SetPath ?? "";
        _projectPath = setPath;   // "" (never saved) → Save asks where
        StampDisk();
        if (!string.IsNullOrWhiteSpace(entry.Meta.Name)) _projectName = entry.Meta.Name;
        SetProjectFolder(setPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(setPath)) : null);
        MarkUnsavedAgainstDisk();
        _recoveryWritten = entry.File;   // removed once it's saved; kept aside if it's let go
        _recoveryIsRecovered = true;
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(WindowTitle));
        Status = $"Recovered unsaved changes ({Cards.Count} card(s)). Save to keep them.";
        return true;
    }

    /// <summary>Unsaved = the cards differ from what's on disk (history position) OR a set-level setting
    /// changed since the last save. Re-derived after every commit/undo, so it must count both.</summary>
    private void RecomputeDirty() => Dirty = _metaDirty || _histIdx != _savedHistIdx;

    /// <summary>What's loaded is NOT what's in the save target (a restored backup): no history position
    /// matches disk, so the ● stays until a Save, whatever is undone or cancelled meanwhile.</summary>
    private void MarkUnsavedAgainstDisk()
    {
        _savedHistIdx = -1;
        Dirty = true;
    }

    private int SelectedIndex() => _selectedCard != null ? Cards.IndexOf(_selectedCard) : -1;
    /// <summary>One undo step: the cards and what the set shares (its settings and house frame) — Set settings is an
    /// edit like any other, and Ctrl+Z after it used to undo an earlier card edit instead.</summary>
    private sealed record Snapshot(List<CardModel> Cards, SetProfile Profile, string DefaultTemplate);
    private string SnapshotJson() => System.Text.Json.JsonSerializer.Serialize(new Snapshot(Cards.ToList(), _setProfile, _defaultTemplate), SnapOpts);

    /// <summary>Restart the debounce so a burst of edits becomes a single undo step.</summary>
    /// <summary>Shows a dialog that edits a card live (Edit details…), holding back the undo timer meanwhile: the
    /// dialog's edits are one undo step, committed when it closes, and a Cancel (which puts the card back) leaves
    /// no step at all — the timer firing inside the dialog used to commit a half-done edit Cancel couldn't undo.</summary>
    internal bool? ShowCardDialog(Window dialog)
    {
        _cardDialogOpen = true;
        _undoTimer.Stop();
        try { return dialog.ShowDialog(); }
        finally { _cardDialogOpen = false; _undoTimer.Stop(); }
    }
    private bool _cardDialogOpen;

    private void QueueUndoCommit()
    {
        if (_restoring || _loading || _cardDialogOpen) return;
        // A job (a lookup, an import…) ends with one CommitHistory of its own; the timer firing part-way through
        // made a half-done step of its own (a looked-up card's text without its art took two Undos). Held until it ends.
        if (_busy) { _undoPending = true; return; }
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private bool _undoPending;   // an edit came in while Busy: queued once the job is over

    /// <summary>Capture the current project state as a new history entry (no-op if nothing changed).</summary>
    internal void CommitHistory()
    {
        if (_restoring || _loading) return;
        _undoTimer.Stop();
        var json = SnapshotJson();
        int sel = SelectedIndex();
        if (_histIdx >= 0 && _history[_histIdx].json == json)
        {
            // Nothing changed — only, perhaps, which card is selected: that isn't an edit (no ●, no empty undo step),
            // but undo comes back to it. Re-derive, so a cancelled edit clears ●.
            _history[_histIdx] = (json, sel);
            RecomputeDirty();
            return;
        }
        if (_histIdx < _history.Count - 1)
        {
            _history.RemoveRange(_histIdx + 1, _history.Count - _histIdx - 1);   // drop the redo tail
            // The saved state was in that tail (saved, then undone): no position matches disk any more, or the
            // new edit would land on the saved index and pass for saved.
            if (_savedHistIdx > _histIdx) _savedHistIdx = -1;
        }
        _history.Add((json, sel));
        _histIdx = _history.Count - 1;
        const int cap = 60;
        while (_history.Count > cap) { _history.RemoveAt(0); _histIdx--; _savedHistIdx--; }
        RecomputeDirty();
        UpdateUndoRedo();
    }

    private void RestoreSnapshot((string json, int sel) snap)
    {
        _restoring = true;
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(snap.json, SnapOpts);
            var cards = state?.Cards ?? new();
            _setProfile = state?.Profile ?? new SetProfile();
            _defaultTemplate = state?.DefaultTemplate ?? "";
            // Undo stays on the side and half being edited: it jumped back to the front, so the next thing typed
            // landed on the wrong face.
            int wasIndex = SelectedIndex();
            var view = (_viewingBack, _viewingFlipped, _readingSideways, _activeHalf, _viewingMelded, _viewingCardBack);
            DetachCardEvents(_selectedCard);
            Cards.Clear();
            foreach (var c in cards) Cards.Add(c);
            _selectedCard = null;
            SelectedCard = (snap.sel >= 0 && snap.sel < Cards.Count) ? Cards[snap.sel] : Cards.FirstOrDefault();
            if (_selectedCard != null && SelectedIndex() == wasIndex)
            {
                (_viewingBack, _viewingFlipped, _readingSideways, _activeHalf, _viewingMelded, _viewingCardBack) = view;
                RefreshCardShape();   // drops a side or half the restored card no longer has
                RenderPreview();
            }
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CanExport));
        // Dirty exactly when we're not sitting on the saved baseline (undo back to saved clears the ●).
        RecomputeDirty();
    }

    public void Undo()
    {
        if (_busy) return;             // CanUndo already blocks the button; Ctrl+Z bypasses it
        CommitHistory();               // flush any pending edit so it can be redone
        if (_histIdx <= 0) return;
        _histIdx--;
        RestoreSnapshot(_history[_histIdx]);
        UpdateUndoRedo();
        Status = "Undo.";
    }

    public void Redo()
    {
        if (_busy) return;             // CanRedo already blocks the button; Ctrl+Y bypasses it
        CommitHistory();               // flush any pending edit first (a new edit correctly cancels redo)
        if (_histIdx >= _history.Count - 1) return;
        _histIdx++;
        RestoreSnapshot(_history[_histIdx]);
        UpdateUndoRedo();
        Status = "Redo.";
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(+1);

    /// <summary>Runs the checks across the whole set, lists every card that needs attention, and jumps to the
    /// first one — so problems in a big set surface without clicking through each card.</summary>
    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) { Status = "No cards to check."; return; }

        var names = Templates.Select(t => t.Name).ToList();
        TemplateSpec ResolveSpec(CardModel c) => TemplateFor(c)?.Spec ?? new TemplateSpec();

        var problems = SetValidator.ValidateAll(Cards, ResolveSpec, names);
        if (problems.Count == 0)
        {
            Status = $"✓ All {Cards.Count} card(s) pass the checks.";
            return;
        }

        const int show = 15;
        var lines = problems.Take(show).Select(p =>
            $"• {p.Card.Name}: {p.Issues[0].Message}" + (p.Issues.Count > 1 ? $"  (+{p.Issues.Count - 1} more)" : ""));
        var more = problems.Count > show ? $"\n…and {problems.Count - show} more card(s)." : "";
        ConfirmDialog.Show(this, $"{problems.Count} of {Cards.Count} card(s) need attention",
            string.Join("\n", lines) + more, affirmative: "OK");

        SelectedCard = problems[0].Card;   // jump to the first problem
        Status = $"{problems.Count} of {Cards.Count} card(s) have issues.";
    }

    /// <summary>Assigns collector numbers in list order: NNN/N (zero-padded to N's width). Guards against
    /// silently clobbering existing (e.g. imported real) numbers — offers renumber-all vs blanks-only.</summary>
    private void OnNumberCards(object sender, RoutedEventArgs e)
    {
        int total = Cards.Count;
        if (total == 0) { Status = "No cards to number."; return; }

        bool onlyBlanks = false;
        if (Cards.Any(c => !string.IsNullOrWhiteSpace(c.CollectorNumber)))
        {
            var choice = ConfirmDialog.Show(this, "Number cards",
                "Some cards already have collector numbers. Renumber every card, or only fill in the blank ones?",
                affirmative: "Renumber all", negative: "Only blanks", cancel: "Cancel");
            if (choice == ConfirmResult.Cancel) return;
            onlyBlanks = choice == ConfirmResult.Negative;
        }

        int changed = CollectorNumbering.Assign(Cards, onlyBlanks);
        MarkDirty();
        CommitHistory();
        RenderPreview();
        Status = onlyBlanks
            ? $"Numbered {changed} blank card(s) (NNN/{total})."
            : $"Numbered {changed} card(s) (NNN/{total}).";
    }

    /// <summary>"Set settings…": what the whole set shares — its printing details, the frame new cards get and the
    /// card back — and whether they reach only new cards, fill the blanks on the cards already here, or replace the
    /// values on all of them (or just the selected ones, when some but not all are selected).</summary>
    private void OnSetSettings(object sender, RoutedEventArgs e)
    {
        var selected = CardList.SelectedItems.Cast<CardModel>().ToList();
        int? some = selected.Count > 1 && selected.Count < Cards.Count ? selected.Count : null;
        var dlg = new SetSettingsWindow(_setProfile, _defaultTemplate, Templates.Select(t => t.Name), BackSetName, some) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ApplySetSettings(dlg.Profile, dlg.DefaultFrame, dlg.Mode, dlg.OnlySelected ? selected : null,
            dlg.ClearSetCode, dlg.ClearArtist, dlg.ClearCopyright, dlg.ClearSetSymbol, dlg.FrameOnEveryCard, dlg.Touched);
    }

    /// <summary>Stores the set's settings and, for <see cref="SetSettingsMode.Replace"/>, puts the fields changed in
    /// this visit on <paramref name="targets"/> (null: every card) — a ticked clear empties it — as one undo step. A field
    /// left as the set already had it isn't stamped on the cards: the window opens filled in with the set's settings,
    /// so changing just the artist must not also put the set's rarity and frame on every card. <paramref name="frameOnEveryCard"/>
    /// puts the frame on them even when it's the one the set already had (re-framing a set whose cards drifted).</summary>
    internal void ApplySetSettings(SetProfile profile, string defaultFrame, SetSettingsMode mode,
        IReadOnlyList<CardModel>? targets = null, bool clearSetCode = false, bool clearArtist = false,
        bool clearCopyright = false, bool clearSetSymbol = false, bool frameOnEveryCard = false,
        IReadOnlyCollection<string>? touched = null)
    {
        if (mode != SetSettingsMode.Replace || Cards.Count == 0)
        {
            ApplySetDefaults(profile, defaultFrame, mode == SetSettingsMode.FillBlanks);
            return;
        }
        var was = _setProfile;
        var wasFrame = _defaultTemplate;
        _setProfile = profile;
        _defaultTemplate = defaultFrame;
        // A changed, filled-in field goes on the cards — changed meaning a value other than the set's, or one set again
        // in this visit (giving more cards the rarity the set already has); a ticked clear empties it on them, and so
        // does picking rarity "(none)"; anything else is left alone.
        bool Touched(string field) => touched?.Contains(field) == true;
        string? Field(string value, string before, bool clear, string field) =>
            clear ? "" : value.Length > 0 && (Touched(field) || !string.Equals(value, before, StringComparison.Ordinal)) ? value : null;
        var list = targets ?? Cards.ToList();
        string? setCode = Field(profile.SetCode, was.SetCode, clearSetCode, SetSettingsWindow.SetCodeField),
            artist = Field(profile.Artist, was.Artist, clearArtist, SetSettingsWindow.ArtistField),
            rarity = Field(profile.Rarity, was.Rarity, profile.Rarity.Length == 0 && Touched(SetSettingsWindow.RarityField), SetSettingsWindow.RarityField),
            copyright = Field(profile.Copyright, was.Copyright, clearCopyright, SetSettingsWindow.CopyrightField),
            frame = frameOnEveryCard && defaultFrame.Length > 0 ? defaultFrame : Field(defaultFrame, wasFrame, false, SetSettingsWindow.FrameField),
            symbol = Field(profile.SetSymbolPath, was.SetSymbolPath, clearSetSymbol, SetSettingsWindow.SymbolField);
        int fields = new[] { setCode, artist, rarity, copyright, frame, symbol }.Count(f => f != null);
        SetFields(list, setCode, artist, rarity, copyright, frame, symbol);   // one undo step, and redraws
        Status = fields == 0
            ? "Saved the set settings — nothing changed to put on the cards."
            : $"Saved the set settings and put the {fields} changed field{(fields == 1 ? "" : "s")} on {list.Count} card{(list.Count == 1 ? "" : "s")}.";
    }

    /// <summary>Stores the set's house defaults (and fills blanks on existing cards if asked).</summary>
    internal void ApplySetDefaults(SetProfile profile, string defaultFrame, bool applyToExisting)
    {
        _setProfile = profile;
        _defaultTemplate = defaultFrame;   // "" clears the house frame
        MarkDirty();                       // the settings are in the undo history, so undoing back to the saved set clears ●

        if (applyToExisting && Cards.Count > 0)
        {
            // Every part of a card: a double-faced card's back and a split card's other half are printed with it.
            int changed = Cards.Count(c => c.Parts().Aggregate(false, (any, part) => _setProfile.ApplyDefaults(part) | any));
            // The default frame fills cards that don't already have one.
            if (!string.IsNullOrWhiteSpace(_defaultTemplate))
                foreach (var part in Cards.SelectMany(c => c.Parts()).Where(p => string.IsNullOrWhiteSpace(p.TemplateName)))
                    part.TemplateName = _defaultTemplate;
            CommitHistory();
            RenderPreview();
            Status = $"Saved the set settings and filled blanks on {changed} existing card(s).";
        }
        else
        {
            CommitHistory();
            RenderPreview();   // the card back lives in the set's settings: no card changed, so nothing else redraws it
            Status = _setProfile.IsEmpty && string.IsNullOrWhiteSpace(_defaultTemplate)
                ? "Cleared the set settings."
                : "Saved the set settings — new and imported cards will get them.";
        }
    }

    /// <summary>Applies bulk field changes to every card and refreshes the live preview. A null value
    /// leaves that field unchanged; an empty string clears it. Exposed for testing the live re-render.</summary>
    internal void ApplyBulkEdit(string? setCode, string? artist, string? rarity,
        string? copyright, string? templateName, string? setSymbolPath)
        => ApplyBulkEdit(Cards.ToList(), setCode, artist, rarity, copyright, templateName, setSymbolPath);

    /// <summary>Applies bulk field changes to a specific set of cards (M14 — a selection, or all), refreshes
    /// the preview, and returns how many cards were targeted. A null value leaves that field unchanged;
    /// an empty string clears it.</summary>
    internal int ApplyBulkEdit(IReadOnlyList<CardModel> targets, string? setCode, string? artist, string? rarity,
        string? copyright, string? templateName, string? setSymbolPath)
        => SetFields(targets, setCode, artist, rarity, copyright, templateName, setSymbolPath);

    private int SetFields(IReadOnlyList<CardModel> targets, string? setCode, string? artist, string? rarity,
        string? copyright, string? templateName, string? setSymbolPath)
    {
        foreach (var c in targets)
        {
            if (templateName != null) SetFrame(c, templateName);
            // The printing details go on every part — a double-faced card's back printed the old set code and artist.
            foreach (var part in c.Parts())
            {
                if (setCode != null) part.SetCode = setCode;
                if (artist != null) part.Artist = artist;
                if (rarity != null) part.Rarity = rarity;
                if (copyright != null) part.Copyright = copyright;
                if (setSymbolPath != null) part.SetSymbolPath = setSymbolPath;
            }
        }
        MarkDirty();
        CommitHistory();
        // Re-sync the frame dropdown to the selected card, whose frame may have changed.
        SyncFramePicker();
        _renderTimer.Stop();   // cancel any pending debounce so our explicit render is the final word
        RenderPreview();       // re-render the active card immediately with the new values
        return targets.Count;
    }

    /// <summary>Moves every selected card up/down as a block (multi-select aware).</summary>
    private void MoveSelected(int delta)
    {
        var sel = CardList.SelectedItems.Cast<CardModel>().ToList();
        if (sel.Count == 0 && _selectedCard != null) sel.Add(_selectedCard);
        if (sel.Count == 0) return;

        var idx = sel.Select(c => Cards.IndexOf(c)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (idx.Count == 0) return;

        if (delta < 0)
        {
            if (idx[0] == 0) return;                                  // topmost already at the top
            foreach (var i in idx) Cards.Move(i, i - 1);
        }
        else
        {
            if (idx[^1] == Cards.Count - 1) return;                  // bottommost already at the bottom
            foreach (var i in Enumerable.Reverse(idx)) Cards.Move(i, i + 1);
        }

        CardList.SelectedItems.Clear();                              // keep the moved cards selected
        foreach (var c in sel) CardList.SelectedItems.Add(c);
        if (_selectedCard != null) CardList.ScrollIntoView(_selectedCard);
        MarkDirty();
        CommitHistory();
        Status = sel.Count > 1 ? $"Moved {sel.Count} cards." : "Reordered cards.";
    }

    private void UpdateUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    internal void RenderPreview() => RenderPreview(inspect: true);   // internal: tests force a render

    /// <param name="inspect">When true, also render a clean (no-hint) bitmap and run the pixel-level
    /// <see cref="RenderInspector"/> for the CHECKS panel (M5). Skipped during live drag to stay smooth.</param>
    private void RenderPreview(bool inspect)
    {
        EndLiveArt();   // a full render shows everything as it is now
        if (_viewingCardBack && _selectedCard != null)
        {
            // The set's card back: what this card shows face down.
            try { PreviewImage = BackRenderer.Render(_setProfile.CardBack, BackSetName, _setProfile.SetSymbolPath, 1); }
            catch (Exception ex) { Status = "Preview error: " + ex.Message; }
            ValidationSummary = "The set's card back"; ValidationDetails = ""; HasValidationIssues = false;
            return;
        }
        var card = PreviewSide;
        if (card == null) { PreviewImage = null; UpdateValidation(null); return; }
        var template = TemplateFor(card);
        if (template == null) return;
        BitmapSource? inspectBmp = null;
        try
        {
            if (_viewingMelded && card.IsMeldBack) card = CardRenderer.MeldedCard(card);   // the whole melded card
            PreviewImage = _renderer.RenderToBitmap(card, template, supersample: 1, previewHints: true);
            if (_viewingFlipped && card.IsFlip)
            {
                // Same card, turned upside down — the flipped half reads upright.
                var turned = new TransformedBitmap(PreviewImage, new System.Windows.Media.RotateTransform(180));
                turned.Freeze();
                PreviewImage = turned;
            }
            else if (_readingSideways && card.IsSplit)
            {
                // Turned a quarter the way it's read: clockwise for a split card, counter-clockwise for the
                // sideways half of an aftermath card.
                var turned = new TransformedBitmap(PreviewImage,
                    new System.Windows.Media.RotateTransform(card.IsAftermath ? 270 : 90));
                turned.Freeze();
                PreviewImage = turned;
            }
            // A clean render (no placeholder hint) so the pixel inspector sees the true art window / border.
            if (inspect)
                inspectBmp = _renderer.RenderToBitmap(card, template, supersample: 1, previewHints: false);
        }
        catch (Exception ex)
        {
            Status = "Preview error: " + ex.Message;
        }
        UpdateValidation(inspectBmp, card, template);
    }

    private void UpdateValidation() => UpdateValidation(null, null, null);

    /// <summary>Runs the automated checks on the active face and updates the CHECKS panel — the friend sees
    /// problems (missing art, bad symbols, overlaps, duplicate collector numbers) as they happen, not on
    /// export. When <paramref name="inspectBmp"/> is supplied, the pixel-level checks (blank art window,
    /// missing/thin border) are merged in too (M5).</summary>
    private void UpdateValidation(BitmapSource? inspectBmp, CardModel? face = null, Template? faceTemplate = null)
    {
        var card = face ?? PreviewSide;
        var template = faceTemplate ?? (card != null ? TemplateFor(card) : null);
        if (card == null || template == null)
        {
            ValidationSummary = ""; ValidationDetails = ""; HasValidationIssues = false;
            return;
        }

        IReadOnlyList<ValidationIssue> issues =
            CardValidator.Validate(card, template.Spec, Cards, Templates.Select(t => t.Name).ToList())
                .Concat(CardValidator.ValidateOtherHalf(card, template.Spec)).ToList();
        if (inspectBmp != null)
        {
            // Pixel inspection is best-effort — never let it break the live panel.
            try { issues = LiveChecks.Merge(issues, RenderInspector.InspectCard(_renderer, card, template, inspectBmp)); }
            catch { /* keep the rule-based issues */ }
        }
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warns = issues.Count(i => i.Severity == IssueSeverity.Warning);
        int notes = issues.Count(i => i.Severity == IssueSeverity.Info);   // no art yet, a shared name…

        if (errors == 0 && warns == 0 && notes == 0)
        {
            ValidationSummary = "✓ No issues";
            ValidationDetails = "";
            HasValidationIssues = false;
            return;
        }

        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors} error{(errors > 1 ? "s" : "")}");
        if (warns > 0) parts.Add($"{warns} warning{(warns > 1 ? "s" : "")}");
        if (notes > 0) parts.Add($"{notes} note{(notes > 1 ? "s" : "")}");
        ValidationSummary = (errors + warns > 0 ? "⚠ " : "✓ ") + string.Join(" · ", parts);
        ValidationDetails = string.Join("\n", issues
            .OrderByDescending(i => i.Severity)
            .Select(i => (i.Severity == IssueSeverity.Info ? "ℹ " : "• ") + i.Message));
        HasValidationIssues = true;
    }

    private void AddAndSelect(CardModel card)
    {
        if (_selectedTemplate != null && string.IsNullOrEmpty(card.TemplateName))
            card.TemplateName = _selectedTemplate.Name;
        _setProfile.ApplyDefaults(card);   // W1: inherit the set's shared metadata (blank fields only)
        Cards.Add(card);
        SelectedCard = card;
    }

    // --- project-level buttons ---------------------------------------------

    private void OnAddCard(object sender, RoutedEventArgs e)
    {
        AddAndSelect(SampleCards.Blank(DefaultTemplateName));
        Status = "Added a blank card.";
    }

    private void OnAddToken(object sender, RoutedEventArgs e)
    {
        AddAndSelect(SampleCards.BlankToken(DefaultTemplateName));
        Status = "Added a token — a type line starting \"Token\" (or \"Emblem\") gives it the token layout.";
    }

    private void OnDuplicateCard(object sender, RoutedEventArgs e)
    {
        if (Busy) return;   // the button is disabled while busy; Ctrl+D would otherwise slip through
        if (_selectedCard == null) return;
        var copy = _selectedCard.Clone();
        int idx = Cards.IndexOf(_selectedCard);
        Cards.Insert(idx + 1, copy);
        SelectedCard = copy;
        Status = "Duplicated card.";
    }

    private void OnDeleteCard(object sender, RoutedEventArgs e)
    {
        // Delete every selected card (multi-select aware), keeping a sensible selection afterwards.
        var sel = CardList.SelectedItems.Cast<CardModel>().ToList();
        if (sel.Count == 0 && _selectedCard != null) sel.Add(_selectedCard);
        if (sel.Count == 0) return;

        int firstIdx = sel.Select(c => Cards.IndexOf(c)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        foreach (var c in sel) Cards.Remove(c);
        SelectedCard = Cards.Count == 0 ? null : Cards[Math.Min(firstIdx, Cards.Count - 1)];
        if (SelectedCard != null) CardList.ScrollIntoView(SelectedCard);
        Status = sel.Count > 1 ? $"Deleted {sel.Count} cards." : "Deleted card.";
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        // Ctrl+N during an import/export would clear the project while the background job keeps filling
        // the cards it captured, then report success over an empty project.
        if (Busy) { Status = "Still working — wait for the current job to finish."; return; }
        if (!ConfirmDiscardIfDirty()) return;
        _loading = true;
        Cards.Clear();
        _projectName = "Untitled Project";
        _projectPath = "";
        StampDisk();
        SetProjectFolder(null);   // ad-hoc until the first Save sets up a set folder
        _defaultTemplate = "";
        _setProfile = new SetProfile();
        _projectUnknown = null;
        _artBaseDir = "";
        AddAndSelect(SampleCards.Blank(DefaultTemplateName));
        _loading = false;
        Dirty = false;
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(WindowTitle));
        Status = "Started a new project.";
        SeedHistory();
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (Busy) { Status = "Still working — wait for the current job to finish."; return; }
        if (!ConfirmDiscardIfDirty()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open project",
            Filter = "Cardinator project or shared set (*.cardinator;*.json;*.zip)|*.cardinator;*.json;*.zip|All files|*.*",
            InitialDirectory = AppPaths.SamplesDir,
        };
        if (dlg.ShowDialog() != true) return;
        if (Path.GetExtension(dlg.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)) OpenSharedSet(dlg.FileName);
        else LoadProjectFile(dlg.FileName);
    }

    /// <summary>Opens a set someone shared (a "Share set + frames" zip): unpacks it into a folder of its own beside
    /// the other sets, installs its frames, and opens it — no unzipping by hand, and nothing saved into a temp folder.</summary>
    internal bool OpenSharedSet(string zipPath)
    {
        string projectPath;
        try { projectPath = SetPackager.UnpackSharedSet(zipPath, AppPaths.SamplesDir); }
        catch (Exception ex) { Status = "Couldn't open that shared set: " + ex.Message; return false; }
        RefreshTemplates();
        if (!LoadProjectFile(projectPath)) return false;
        Status += $"  Unpacked to {Path.GetDirectoryName(projectPath)}.";
        return true;
    }

    /// <summary>Loads a project file into the window. Returns false (leaving the current project untouched)
    /// when the file couldn't be read, so callers can avoid mutating state on a failed load.</summary>
    private bool LoadProjectFile(string path)
    {
        try
        {
            // Image paths are stored relative to the SET folder (a backup's: the set one level up), resolved here —
            // otherwise restoring a backup would point every card at a non-existent backups\art\… file.
            var project = CardProject.LoadResolved(path, out var projFolder);
            _loading = true;
            Cards.Clear();
            foreach (var c in project.Cards) Cards.Add(c);
            _projectName = string.IsNullOrWhiteSpace(project.Name)
                ? Path.GetFileNameWithoutExtension(path) : project.Name;
            _projectPath = path;
            SetProjectFolder(projFolder);
            _artBaseDir = project.ArtBaseDir;
            // Restore the set's default frame so new/imported cards inherit the house style (M4). Kept as saved
            // even when it isn't installed here (DefaultTemplateName falls back for new cards), so saving on a
            // machine without that frame doesn't rewrite the set's house frame.
            _defaultTemplate = project.DefaultTemplate ?? "";
            // Restore the set's shared metadata defaults (W1); LoadResolved already made its symbol path absolute.
            _setProfile = project.Profile;
            _projectUnknown = project.Unknown;
            SelectedCard = Cards.FirstOrDefault();
            _loading = false;
            Dirty = false;
            StampDisk();
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            int missingArt = SetFolder.CountMissingArt(Cards);
            Status = $"Opened project with {Cards.Count} card(s)."
                     + (missingArt > 0 ? $"  ⚠ {missingArt} card(s) have missing art (was the art/ folder included?)." : "");
            SeedHistory();

            // Opened straight out of a backups\ folder (File > Open or a drop, not Restore): Save must go to the
            // set file, not into backups\ — and until it does, what's loaded isn't what's on disk.
            if (ProjectBackup.ProjectFileForBackup(path) is { } setFile)
            {
                _projectPath = setFile;   // "" (an oddly named file) → Save asks where
                StampDisk();
                if (setFile.Length > 0) _projectName = Path.GetFileNameWithoutExtension(setFile);
                MarkUnsavedAgainstDisk();
                OnPropertyChanged(nameof(ProjectSummary));
                Status = setFile.Length > 0
                    ? $"Opened a backup. Use Save to restore it over “{Path.GetFileName(setFile)}”."
                    : "Opened a backup. Use Save as… to keep it.";
            }

            // Written by a newer Cardinator: it loaded (we always read old AND unknown fields tolerantly),
            // but saving from here would quietly drop whatever this build doesn't understand.
            if (project.IsFromNewerVersion)
            {
                ConfirmDialog.Show(this, "Saved by a newer Cardinator",
                    $"“{Path.GetFileName(path)}” was saved by a newer version of Cardinator.\n\n"
                    + "It opened fine, but anything this version doesn't know about won't be kept if you "
                    + "save over it. Update Cardinator first if you want to keep everything.\n\n"
                    + "(Every save also keeps a backup in the set's backups folder.)", affirmative: "OK");
                Status += "  ⚠ Saved by a newer version — saving here may drop newer data.";
            }
            return true;
        }
        catch (NotAProjectException ex)
        {
            _loading = false;   // nothing was touched — the set that's loaded stays loaded
            ConfirmDialog.Show(this, "Not a set",
                ex.Message + "\n\nIt looks like a card or frame file, not a set saved by Cardinator, so it wasn't "
                + "opened (saving would have overwritten it).", affirmative: "OK");
            Status = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _loading = false;
            // The file exists but couldn't be parsed — preserve it before the user does anything that
            // might overwrite it, and point them at the backup (L9).
            var backup = IoUtil.BackupCorrupt(path);
            if (backup != null)
            {
                ConfirmDialog.Show(this, "Couldn't open project",
                    $"“{Path.GetFileName(path)}” couldn't be read — it may be corrupt or from a newer version.\n\n"
                    + $"A copy was saved as “{Path.GetFileName(backup)}” so nothing is lost.\n\n"
                    + "Details: " + ex.Message, affirmative: "OK");
                Status = $"Couldn't open project (backed up as {Path.GetFileName(backup)}).";
            }
            else
            {
                Status = "Couldn't open project: " + ex.Message;
            }
            return false;
        }
    }

    private void OnSaveProject(object sender, RoutedEventArgs e) => SaveProject(forceDialog: false);

    private void OnSaveProjectAs(object sender, RoutedEventArgs e)
    {
        if (Busy) return;   // the button is disabled while busy; Ctrl+Shift+S would otherwise slip through
        SaveProject(forceDialog: true);
    }

    /// <summary>Opens one of this set's automatic backups (made on each save) after confirming any unsaved
    /// work. Lets the user roll back a bad save / buggy build without hunting through the file system.</summary>
    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        if (string.IsNullOrEmpty(_projectPath))
        {
            Status = "Save the set once first — backups are created each time you save.";
            return;
        }
        var backups = ProjectBackup.ListBackups(_projectPath);
        if (backups.Count == 0) { Status = "No backups yet — they're created each time you save."; return; }
        if (!ConfirmDiscardIfDirty()) return;

        // Show the most recent backups (newest first) and let the user pick one to open read-into-memory.
        var dlg = new OpenFileDialog
        {
            Title = "Restore a backup of this set",
            InitialDirectory = ProjectBackup.BackupDir(_projectPath),
            Filter = "Cardinator backups (*.cardinator;*.json)|*.cardinator;*.json|All files|*.*",
            FileName = Path.GetFileName(backups[0]),
        };
        if (dlg.ShowDialog() != true) return;

        // Keep the real project file as the save target so one plain Save restores the backup IN PLACE (that
        // save itself backs up the bad version first). On a failed load nothing changes — the project that's
        // still loaded keeps its path and its clean/dirty state.
        var realPath = _projectPath;
        var realName = _projectName;
        if (!LoadProjectFile(dlg.FileName)) return;

        _projectPath = realPath;
        _projectName = realName;
        StampDisk();
        MarkUnsavedAgainstDisk();
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(WindowTitle));
        Status = $"Loaded backup “{Path.GetFileName(dlg.FileName)}”. Use Save to restore it over “{Path.GetFileName(realPath)}”.";
    }

    /// <summary>Saves to the remembered path when we have one; otherwise prompts for a location.</summary>
    private bool SaveProject(bool forceDialog)
    {
        var path = _projectPath;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save project",
                Filter = "Cardinator project (*.cardinator)|*.cardinator|JSON (*.json)|*.json",
                FileName = SafeName(_projectName) + ".cardinator",
                InitialDirectory = SaveDialogFolder(),
            };
            if (dlg.ShowDialog() != true) return false;
            path = SetFolder.OwnFolderPath(dlg.FileName);   // into a folder of its own when the picked one holds other things
            if (!string.Equals(path, Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var replace = ConfirmDialog.Show(this, "Save set",
                    $"There's already a set at {path}. Replace it with this one? (It's backed up first.)",
                    affirmative: "Replace", cancel: "Cancel");
                if (replace != ConfirmResult.Affirmative) return false;
            }

            // Warn before two sets share one folder (their art/ and out/ would mingle / overwrite).
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder != null && SetFolder.ContainsOtherProject(folder, path))
            {
                var ok = ConfirmDialog.Show(this, "Save set",
                    "This folder already contains another project — they'll share the same art and output folders "
                    + "(exports can overwrite each other). Save here anyway, or pick a new folder?",
                    affirmative: "Save here", cancel: "Cancel");
                if (ok != ConfirmResult.Affirmative) return false;
            }
        }
        else if (SaveConflict() is { } conflict)
        {
            var choice = ConfirmDialog.Show(this, "Save set", conflict.Message,
                affirmative: "Save as…", negative: conflict.Moved ? "Save there anyway" : "Overwrite", cancel: "Cancel");
            if (choice == ConfirmResult.Cancel) return false;
            if (choice == ConfirmResult.Affirmative) return SaveProject(forceDialog: true);
        }
        CommitHistory();   // fold in edits still waiting on the undo debounce, so the saved point IS what's written
        try
        {
            // The file name IS the project's identity (there's no separate name field), so persist the
            // name derived from the path — otherwise a reloaded file would show a stale/"Untitled" name.
            var name = Path.GetFileNameWithoutExtension(path);
            var projFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(projFolder);

            // Localize art → relative paths → back up the previous good file → atomic write. That exact
            // order is the data-safety guarantee, so it lives in ProjectWriter where it can be tested.
            int stranded = ProjectWriter.Write(path, Cards.ToList(), name, _artBaseDir, _defaultTemplate, _setProfile,
                previousFolder: _projectFolder, unknown: _projectUnknown);
            int missing = SetFolder.CountMissingArt(Cards);
            // Writing copied outside art into the set and re-pointed the cards at the copies. That's what's on disk
            // now, so it's what this history position holds — not a new edit that brings the ● straight back.
            _undoTimer.Stop();
            if (_histIdx >= 0) _history[_histIdx] = (SnapshotJson(), SelectedIndex());
            _projectPath = path;
            StampDisk();
            SetProjectFolder(projFolder);
            _projectName = name;
            MarkSavedPoint();   // this history position now matches disk (undo past it re-marks dirty)
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            Status = stranded > 0
                ? $"Saved project ({Cards.Count} cards), but {stranded} image(s) couldn't be copied into the set — they won't travel if you move the folder."
                : missing > 0
                    ? $"Saved project ({Cards.Count} cards) to {Path.GetFileName(path)}, but {missing} card face(s) point at art that isn't there — use Restore… if the set's folder was moved."
                    : $"Saved project ({Cards.Count} cards) to {path}. Art in \\art, exports go to \\out.";
            return true;
        }
        catch (Exception ex)
        {
            Status = "Couldn't save project: " + ex.Message;
            return false;
        }
    }

    /// <summary>Where the Save dialog opens: the set's own folder, or — when that folder was moved or renamed — the
    /// folder it was in, so "Save as… to where the set is now" starts next to it; Documents\Cardinator Sets for a new set.</summary>
    private string SaveDialogFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(_projectFolder))
            {
                if (Directory.Exists(_projectFolder)) return _projectFolder;
                var parent = Path.GetDirectoryName(Path.GetFullPath(_projectFolder).TrimEnd(Path.DirectorySeparatorChar));
                if (parent != null && Directory.Exists(parent)) return parent;
            }
        }
        catch { }
        return AppPaths.NewSetsDir;
    }

    private (string Path, DateTime Written, long Length)? _diskStamp;   // the set file as opened / last saved here

    /// <summary>Notes the set file's time and size as it is now, so Save can tell when something else changed it.</summary>
    private void StampDisk()
    {
        try
        {
            var fi = new FileInfo(_projectPath);
            _diskStamp = _projectPath.Length > 0 && fi.Exists ? (Path.GetFullPath(_projectPath), fi.LastWriteTimeUtc, fi.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _diskStamp = null; }
    }

    /// <summary>Why a plain Save to the set's own file needs asking about, or null: the set's folder or file has gone
    /// since it was opened (moved or renamed — saving would re-create the old folder with the new file and none of the
    /// art), or something else saved the file since (another window, a command, a script — saving would replace that).</summary>
    internal (string Message, bool Moved)? SaveConflict()
    {
        if (_diskStamp is not { } stamp || _projectPath.Length == 0) return null;
        string path;
        try { path = Path.GetFullPath(_projectPath); } catch { return null; }
        if (!string.Equals(path, stamp.Path, StringComparison.OrdinalIgnoreCase)) return null;
        var fi = new FileInfo(path);
        if (!Directory.Exists(fi.DirectoryName))
            return ($"The set's folder “{fi.DirectoryName}” isn't there any more — was it moved or renamed? Saving here would "
                    + "make a new folder holding only the set file, without its art. Save as… to the folder where the set is now.", true);
        if (!fi.Exists)
            return ($"“{fi.Name}” isn't in its folder any more — was it moved or renamed? Save as… to where it is now, or save it here again.", true);
        if (fi.LastWriteTimeUtc != stamp.Written || fi.Length != stamp.Length)
            return ($"“{fi.Name}” was changed and saved somewhere else since you opened it (another window, a command or a script). "
                    + "Saving replaces those changes (they stay in its backups). Save as… keeps both.", false);
        return null;
    }

    /// <summary>Switches the set folder. A folder exported to while working on another set (or before the first
    /// Save) is forgotten, so exports default to THIS set's <c>out/</c> rather than the last set's.</summary>
    private void SetProjectFolder(string? folder)
    {
        static string? Norm(string? f) => string.IsNullOrEmpty(f) ? null : Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Norm(_projectFolder), Norm(folder), StringComparison.OrdinalIgnoreCase))
            _lastExportDir = null;
        _projectFolder = folder;
    }

    /// <summary>Where exports should default: the last place you exported, else the set's <c>out</c> folder,
    /// else the app's shared output dir.</summary>
    private string DefaultOutputDir()
    {
        if (!string.IsNullOrWhiteSpace(_lastExportDir) && Directory.Exists(_lastExportDir)) return _lastExportDir!;
        if (_projectFolder != null)
        {
            var outDir = Path.Combine(_projectFolder, "out");
            try { Directory.CreateDirectory(outDir); } catch { /* fall through */ }
            if (Directory.Exists(outDir)) return outDir;
        }
        return AppPaths.OutputDir;
    }

    /// <summary>Returns true if it's OK to proceed (nothing unsaved, or the user chose to discard/saved).</summary>
    private bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        var choice = ConfirmDialog.Show(this, "Unsaved changes",
            "You have unsaved changes. Save them before continuing?",
            affirmative: "Save", negative: "Don't save", cancel: "Cancel");
        return choice switch
        {
            ConfirmResult.Affirmative => SaveProject(forceDialog: false),
            ConfirmResult.Negative => DiscardChanges(),
            _ => false,
        };
    }

    private bool DiscardChanges()
    {
        ClearRecovery();
        return true;
    }

    // --- import & batch -----------------------------------------------------

    private async Task ImportListFile(string filePath)
    {
        if (Busy) return;
        ImportResult parsed;
        string baseDir;
        try
        {
            var content = ImportService.ReadListFile(filePath);
            baseDir = Path.GetDirectoryName(filePath) ?? "";
            parsed = ImportService.ParseWithReport(content, baseDir, DefaultTemplateName);
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; return; }

        if (parsed.Cards.Count == 0)
        {
            Status = parsed.Skipped > 0
                ? $"No cards found in that file ({parsed.Skipped} line(s) couldn't be read)."
                : "No cards found in that file.";
            return;
        }
        _artBaseDir = baseDir;   // only commit the base dir once we know the import actually produced cards
        await AddImportedAsync(parsed.Cards, "that file", parsed.Skipped);
    }

    /// <summary>"Import cards…": a Moxfield deck by link, a list file, or a pasted list — all land as text in the
    /// dialog and flow through the same parser (see <see cref="DeckListWindow"/>); a file's art paths are read from
    /// beside it.</summary>
    private async void OnImportDeckList(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dlg = new DeckListWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await ImportListText(dlg.DeckText, dlg.BaseDir, dlg.FileName);
    }

    internal async Task ImportListText(string text, string baseDir, string fileName)
    {
        var parsed = ImportService.ParseWithReport(text, baseDir, DefaultTemplateName);
        if (parsed.Cards.Count == 0)
        {
            Status = parsed.Skipped > 0
                ? $"No cards found in the list ({parsed.Skipped} line(s) couldn't be read)."
                : "No cards found in the list.";
            return;
        }
        if (baseDir.Length > 0) _artBaseDir = baseDir;   // only once the import produced cards
        await AddImportedAsync(parsed.Cards, fileName.Length > 0 ? fileName : "the list", parsed.Skipped);
    }

    /// <summary>Adds parsed cards to the project and fills their blank fields + art from Scryfall,
    /// with a live progress bar. Shared by file import and deck-list import.</summary>
    private async Task AddImportedAsync(List<ImportedCard> imported, string source, int skipped = 0)
    {
        if (Busy) return;

        // L3: re-importing a list you already loaded would silently append duplicates. Offer to skip them.
        int dupes = ImportService.CountNamesAlreadyIn(Cards, imported);
        if (dupes > 0)
        {
            var choice = ConfirmDialog.Show(this, "Cards already in project",
                $"{dupes} of these {imported.Count} card(s) have a name already in your project. "
                + "Skip the duplicates, or add them anyway?",
                affirmative: "Skip duplicates", negative: "Add anyway", cancel: "Cancel");
            if (choice == ConfirmResult.Cancel) { Status = "Import cancelled."; return; }
            if (choice == ConfirmResult.Affirmative)
            {
                imported = ImportService.RemoveNamesAlreadyIn(Cards, imported);
                if (imported.Count == 0) { Status = "Nothing to import — all cards were duplicates."; return; }
            }
        }

        Busy = true;
        ProgressIndeterminate = false;   // we know the card count, so show real percentage
        ExportProgress = 0;
        try
        {
            foreach (var item in imported) { _setProfile.ApplyDefaults(item.Card); Cards.Add(item.Card); }   // W1 inherit set defaults
            SelectedCard = imported[0].Card;
            OnPropertyChanged(nameof(ProjectSummary));

            int needLookup = imported.Count(i => i.NeedsLookup);
            Status = $"Imported {imported.Count} card(s) from {source}. Filling {needLookup} from Scryfall…";

            var progress = new Progress<string>(s => Status = s);
            var percent = new Progress<double>(p => ExportProgress = p * 100);
            var report = await BatchService.FillFromScryfallAsync(imported, progress, percent: percent);
            foreach (var item in imported)   // backs the lookup attached inherit the set's metadata like their fronts
                if (item.Card.BackFace is { } back) _setProfile.ApplyDefaults(back);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            MarkDirty();
            CommitHistory();   // fields filled on non-selected cards don't trip OnCardChanged, so commit explicitly
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            int dfc = imported.Count(i => i.Card.IsDoubleFaced);   // DFCs now import as one card with a back face
            Status = $"Imported {imported.Count} card(s). Scryfall filled {report.Filled}"
                     + (dfc > 0 ? $" ({dfc} double-faced)" : "")
                     + (report.Offline ? "; couldn't reach Scryfall, so some are names only — Fill in blanks from Scryfall when you're online" : "")
                     + (report.NotFound.Count > 0 && !report.Offline ? $"; not found: {NameList(report.NotFound)}" : "")
                     + (skipped > 0 ? $"; skipped {skipped} unreadable line(s)" : "")
                     + ".";
        }
        catch (Exception ex)
        {
            Status = "Import failed: " + ex.Message;
        }
        finally { ProgressIndeterminate = true; Busy = false; }
    }

    private async void OnScryfallSearch(object sender, RoutedEventArgs e)
    {
        // Show results in a picker first; only the cards the user checks get added.
        var win = new ScryfallSearchWindow(_scryfall) { Owner = this };
        if (win.ShowDialog() != true) return;
        var chosen = win.SelectedCards;
        if (chosen.Count == 0) return;
        if (Busy) return;
        Busy = true;
        try
        {
            var def = DefaultTemplateName;
            foreach (var c in chosen)
            {
                CardDetailsFill.TakeFrame(c, def);
                _setProfile.ApplyDefaults(c);   // W1: inherit the set's shared metadata, like every other add path
                if (c.BackFace != null) _setProfile.ApplyDefaults(c.BackFace);
                Cards.Add(c);
            }
            SelectedCard = chosen[0];
            OnPropertyChanged(nameof(ProjectSummary));
            _ = _symbols.PrimeAsync(chosen.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));

            Status = $"Added {chosen.Count} card(s). Downloading art…";
            int art = 0;
            foreach (var c in chosen)
            {
                if (c.BackFace is { ArtPath: var bp, ArtUrl: var bu } back && string.IsNullOrWhiteSpace(bp) && !string.IsNullOrWhiteSpace(bu))
                    try { back.ArtPath = await ImageIntake.DownloadAsync(bu); } catch { /* skip */ }   // a double-faced card's back
                if (!string.IsNullOrWhiteSpace(c.ArtPath) || string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
                try { c.ArtPath = await ImageIntake.DownloadAsync(c.ArtUrl); art++; } catch { /* skip */ }
                CardDetailsFill.SplitSharedArt(c);   // a split card's art holds both halves: give each its side
            }
            MarkDirty();
            CommitHistory();
            RenderPreview();
            Status = $"Added {chosen.Count} card(s) from search ({art} with art).";
        }
        catch (Exception ex)
        {
            Status = "Search failed: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private async void OnLookupMissing(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var items = Cards.Select(c => new ImportedCard(
            c, string.IsNullOrWhiteSpace(c.ManaCost)
               && string.IsNullOrWhiteSpace(c.TypeLine)
               && string.IsNullOrWhiteSpace(c.RulesText))).ToList();
        int n = items.Count(i => i.NeedsLookup);
        if (n == 0) { Status = "No cards are missing text to look up."; return; }

        Busy = true;
        ProgressIndeterminate = false;
        ExportProgress = 0;
        Status = $"Filling {n} card(s) from Scryfall…";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var percent = new Progress<double>(p => ExportProgress = p * 100);
            var report = await BatchService.FillFromScryfallAsync(items, progress, percent: percent, keepNames: true);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            foreach (var item in items)   // backs the fill attached inherit the set's metadata like their fronts
                if (item.NeedsLookup && item.Card.BackFace is { } back) _setProfile.ApplyDefaults(back);
            MarkDirty();
            CommitHistory();   // fields filled in place don't trip OnCardChanged for non-selected cards
            RefreshCardShape();   // the selected card may have gained a back face or another half
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            int dfc = items.Count(i => i.Card.IsDoubleFaced);   // DFCs fill as one card with a back face
            Status = $"Filled {report.Filled} card(s)"
                     + (dfc > 0 ? $" ({dfc} double-faced)" : "")
                     + (report.Offline ? "; couldn't reach Scryfall for the rest — try again when you're online."
                        : report.NotFound.Count > 0 ? $"; not found: {NameList(report.NotFound)}." : ".");
        }
        catch (Exception ex)
        {
            Status = "Fill from Scryfall failed: " + ex.Message;
        }
        finally { ProgressIndeterminate = true; Busy = false; }
    }

    /// <summary>"A; B; C; D; E and 3 more" — names in a status line, without it running off the window.</summary>
    internal static string NameList(IReadOnlyList<string> names, int show = 5) =>
        string.Join("; ", names.Take(show)) + (names.Count > show ? $" and {names.Count - show} more" : "");

    /// <summary>" 2 card face(s) have art that's missing or can't be read …" after an export, or "".</summary>
    private static string ArtProblemNote(int faces) => faces == 0 ? ""
        : $" {faces} card face(s) have art that's missing or can't be read — their art windows are empty (Check all cards lists them).";

    private async void OnExportAll(object sender, RoutedEventArgs e)
    {
        if (IsExporting) { Status = "An export is already running…"; return; }
        if (Cards.Count == 0) { Status = "No cards to export."; return; }
        var dlg = new OpenFolderDialog { Title = "Choose a folder to export all cards into", InitialDirectory = DefaultOutputDir() };
        if (dlg.ShowDialog() != true) return;

        // Deep-clone so edits made during the export can't tear reads on the render thread.
        var cardsSnapshot = Cards.Select(c => c.Clone()).ToList();
        var templatesSnapshot = Templates.ToList();
        var houseFrame = _defaultTemplate;
        var symbols = _symbols;
        var folder = dlg.FolderName;

        Busy = true;
        IsExporting = true;
        ProgressIndeterminate = false;
        ExportProgress = 0;
        Status = $"Exporting {cardsSnapshot.Count} card(s)…";
        try
        {
            // Make sure the symbols these cards use are downloaded first.
            var tokens = cardsSnapshot.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            await symbols.PrimeAsync(tokens);

            var strProgress = new Progress<string>(s => Status = s);
            var pctProgress = new Progress<double>(p => ExportProgress = p * 100);

            // Rendering needs an STA thread; run it off the UI thread so the window stays responsive.
            // Every card of the set: pictures an earlier export left (of cards since deleted or moved) go.
            var result = await RunStaAsync(() =>
                BatchService.ExportAll(cardsSnapshot, templatesSnapshot, folder, symbols, strProgress, pctProgress, houseFrame, wholeSet: true));

            _lastExportDir = folder;   // so "Open output folder" follows an Export-all
            int artProblems = ArtCheck.Notes(cardsSnapshot).Count;
            Status = $"Exported {result.Exported}/{cardsSnapshot.Count - result.Repeats} to {folder}."
                     + (result.Repeats > 0 ? $" {result.Repeats} repeated card(s) exported once." : "")
                     + (result.Errors.Count > 0 ? $" {result.Errors.Count} not exported — {result.Errors[0]}{(result.Errors.Count > 1 ? " …" : "")}" : "")
                     + ArtProblemNote(artProblems);
        }
        catch (Exception ex)
        {
            Status = "All as images failed: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
            Busy = false;
        }
    }

    /// <summary>Zips the whole set — project file + art/ + a bundle for each custom frame the cards use — so it
    /// can be handed to someone else who doesn't have your frames. Requires the project to be saved first.</summary>
    private void OnExportSetBundle(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) { Status = "No cards to share."; return; }
        if (string.IsNullOrEmpty(_projectPath) || !File.Exists(_projectPath))
        {
            Status = "Save the project first — Share set bundles the saved set folder.";
            return;
        }
        // The zip is made from the saved files, so unsaved changes would be left out of it.
        if (Dirty)
        {
            if (ConfirmDialog.Show(this, "Save before sharing",
                    "This set has unsaved changes. Share set packs the saved set, so save it first?",
                    affirmative: "Save and share", cancel: "Cancel") != ConfirmResult.Affirmative)
                return;
            if (!SaveProject(forceDialog: false)) return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Share set + frames",
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = SafeName(_projectName) + "-set.zip",
            InitialDirectory = DefaultOutputDir(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int frames = SetPackager.ExportSetWithFrames(_projectPath, ShareFrameFolders(), dlg.FileName);
            _lastExportDir = Path.GetDirectoryName(dlg.FileName);
            Status = $"Shared set to {Path.GetFileName(dlg.FileName)} ({Cards.Count} cards, {frames} custom frame(s) included).";
        }
        catch (Exception ex)
        {
            Status = "Couldn't share set: " + ex.Message;
        }
    }

    /// <summary>The folders of the frames the set's cards use, found from the loaded frames (a frame renamed in place
    /// keeps its old folder name), else guessed from the name.</summary>
    internal List<string> ShareFrameFolders() => Cards.SelectMany(TemplateService.FramesUsed).Distinct()
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(n => Templates.FirstOrDefault(t => t.Name == n) is { FramePath: { Length: > 0 } fp }
            ? Path.GetDirectoryName(fp) ?? "" : Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(n)))
        .Where(d => d.Length > 0).ToList();

    private async void OnExportSheet(object sender, RoutedEventArgs e)
    {
        if (IsExporting) { Status = "An export is already running…"; return; }
        if (Cards.Count == 0) { Status = "No cards to export."; return; }

        // Offer double-sided (adds a mirrored back page after each front page for duplex printing).
        var duplexAnswer = ConfirmDialog.Show(this, "Print sheet",
            "Include card backs for double-sided printing?\n\n"
            + "Front + back — pages to flip on the long edge.\nFronts only — just the card fronts.",
            affirmative: "Front + back", negative: "Fronts only", cancel: "Cancel");
        if (duplexAnswer == ConfirmResult.Cancel) return;
        bool doubleSided = duplexAnswer == ConfirmResult.Affirmative;

        var dlg = new OpenFolderDialog { Title = "Choose a folder for the printable sheet pages", InitialDirectory = DefaultOutputDir() };
        if (dlg.ShowDialog() != true) return;

        // Deep-clone so edits during the (background) compose can't tear reads on the render thread.
        var cardsSnapshot = Cards.Select(c => c.Clone()).ToList();
        var templatesSnapshot = Templates.ToList();
        var houseFrame = _defaultTemplate;
        var backDesign = _setProfile.CardBack?.Clone();
        var backName = BackSetName;
        var backSymbol = _setProfile.SetSymbolPath;
        var symbols = _symbols;
        var folder = dlg.FolderName;

        Busy = true;
        IsExporting = true;
        ExportProgress = 0;
        Status = $"Composing print sheet for {cardsSnapshot.Count} card(s)…";
        try
        {
            var tokens = cardsSnapshot.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            await symbols.PrimeAsync(tokens);
            var strProgress = new Progress<string>(s => Status = s);

            var paths = await RunStaAsync(() =>
            {
                var pages = doubleSided
                    ? SheetExporter.ComposeDoubleSided(cardsSnapshot, templatesSnapshot, symbols, PageSpec.Letter, BackRenderer.Render(backDesign, backName, backSymbol, 1), strProgress, houseFrame)
                    // Single-sided: expand DFC cards so both faces get a printable slot.
                    : SheetExporter.Compose(BatchService.ExpandFaces(cardsSnapshot), templatesSnapshot, symbols, PageSpec.Letter, strProgress, houseFrame);
                return SheetExporter.Save(pages, folder);
            });
            ExportProgress = 100;
            _lastExportDir = folder;   // so "Open output folder" opens where the sheet went
            Status = (doubleSided
                ? $"Saved {paths.Count} page(s) — front+back, {folder}."
                : $"Saved {paths.Count} sheet page(s) (3x3) to {folder}.") + ArtProblemNote(ArtCheck.Notes(cardsSnapshot).Count);
        }
        catch (Exception ex)
        {
            Status = "Sheet export failed: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
            Busy = false;
        }
    }

    /// <summary>Edits the side or half being shown (<see cref="EditedFace"/>): the card, its back face, or its other half.</summary>
    private void OnEditDetails(object sender, RoutedEventArgs e)
    {
        if (_selectedCard is not { } card || EditedFace is not { } face) return;
        ShowCardDialog(ReferenceEquals(face, card.OtherHalf)
            ? new DetailsWindow(face, _scryfall, card.HalfLayout) { Owner = this }
            : new DetailsWindow(face, _scryfall) { Owner = this });
        CommitHistory();   // coalesce the dialog's edits into one undo step and refresh the preview
        RefreshCardShape();   // its lookup may have added a back face or another half
        RenderPreview();
    }

    /// <summary>Re-syncs everything that depends on the selected card's shape — the back face, flip/split and meld
    /// controls, the meld partner and the frame picker — after something other than those controls changed it
    /// (a lookup, the details dialog, a batch fill).</summary>
    private void RefreshCardShape()
    {
        if (_selectedCard is { } card)
        {
            if (card.BackFace == null) { _viewingBack = false; _viewingMelded = false; }
            else if (_viewingCardBack) { _viewingCardBack = false; _viewingBack = true; }   // it has a back face of its own now
            if (card.OtherHalf == null) { _viewingFlipped = false; _readingSideways = false; _activeHalf = false; }
            _meldPartner = MeldPartner(card);
        }
        SyncFramePicker();
        RefreshCardControls();
    }

    private void OnHelp(object sender, RoutedEventArgs e)
        => new HelpWindow(App.Version) { Owner = this }.ShowDialog();

    private async void OnImportFrame(object sender, RoutedEventArgs e)
    {
        // Two clearly visible choices, intuitive for a first-time user: a "Choose file…" button
        // (a PNG image, a .cardframe, or a .zip bundle from the PC) OR a pasted web link to an image.
        var prompt = new InputDialog("Import a frame or template",
            "Choose a file from your PC — a frame image (PNG), or a shared template bundle (.cardframe or .zip) — "
            + "or paste a web link to a frame image below.")
        { Owner = this };
        prompt.ShowBrowse(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a frame image, or a shared template bundle",
                Filter = "Frames & templates|*.cardframe;*.zip;*.png;*.webp;*.gif;*.bmp"
                       + "|Template bundle|*.cardframe;*.zip|Images|*.png;*.webp;*.gif;*.bmp|All files|*.*",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });
        if (prompt.ShowDialog() != true) return;   // cancelled
        try
        {
            string name;
            if (prompt.PickedFile)
            {
                var path = prompt.Value;
                // A bundle carries its own tuned regions/fonts/colors; a bare image gets default regions.
                if (TemplateImporter.IsBundlePath(path)) { ImportFrameBundle(path); return; }
                else
                {
                    var fullArt = AskFrameFullArt();
                    if (fullArt == null) { Status = "Import cancelled."; return; }
                    name = TemplateImporter.CreateFromFile(Path.GetFileNameWithoutExtension(path), path, fullArt.Value);
                    Status = $"Imported frame as template \"{name}\". Click Design… to fit its text regions to the picture.";
                }
            }
            else
            {
                var url = prompt.Value;
                if (url.Length == 0) { Status = "Nothing to import — choose a file or paste a link."; return; }
                if (!ImageIntake.IsHttpUrl(url)) { Status = "That doesn't look like a web link."; return; }
                var fullArt = AskFrameFullArt();
                if (fullArt == null) { Status = "Import cancelled."; return; }
                Status = "Downloading frame…";
                name = await TemplateImporter.CreateFromUrlAsync("", url, fullArt.Value);
                Status = $"Imported frame as template \"{name}\". Click Design… to fit its text regions to the picture.";
            }
            ShowImportedFrame(name);
        }
        catch (Exception ex)
        {
            Status = "Couldn't import: " + ex.Message;
        }
    }

    /// <summary>Installs a shared template bundle (.cardframe or .zip) and picks it — from Import frame… or a
    /// bundle dropped onto the window. Throws if it can't be imported.</summary>
    private void ImportFrameBundle(string path)
    {
        var names = TemplateImporter.ImportBundles(path);
        var name = names[0];
        Status = names.Count == 1
            ? $"Imported template \"{name}\"."
            : $"Imported {names.Count} templates: {string.Join(", ", names.Select(n => $"\"{n}\""))}.";
        ShowImportedFrame(name);
    }

    /// <summary>Refreshes the frame list onto a just-imported frame, saying so if that put it on the card.</summary>
    private void ShowImportedFrame(string name)
    {
        bool reframes = _selectedCard != null && _selectedCard.TemplateName != name;
        RefreshTemplates(name);
        if (reframes && _selectedCard != null && _selectedCard.TemplateName == name)
            Status += $" Put it on “{_selectedCard.Name}” (Ctrl+Z to undo).";
    }

    /// <summary>Asks whether a bare imported frame image is a full-art frame (text on the art) or a standard
    /// framed layout, so the new template gets the right default regions (L11). Returns null on cancel.</summary>
    private bool? AskFrameFullArt()
    {
        var choice = ConfirmDialog.Show(this, "Frame type",
            "Is this a full-art frame (the art fills the whole card and the text sits on top), "
            + "or a standard frame with a separate art window?",
            affirmative: "Full-art", negative: "Standard frame", cancel: "Cancel");
        return choice switch
        {
            ConfirmResult.Affirmative => true,
            ConfirmResult.Negative => false,
            _ => null,
        };
    }

    /// <summary>The frame in the picker. With no card selected an empty picker means the first frame; with one, an
    /// empty picker means the card's frame isn't installed — nothing to act on.</summary>
    private Template? PickedFrame() => _selectedTemplate ?? (PreviewSide == null ? Templates.FirstOrDefault() : null);

    private string NoFramePicked(string verb) => PreviewSide is { TemplateName.Length: > 0 } face
        ? $"This card's frame “{face.TemplateName}” isn't installed — pick a frame first, then {verb} it."
        : $"No frame selected to {verb}.";

    /// <summary>Exports the selected template as a shareable bundle (frame.png + template.json in one file).</summary>
    private void OnExportTemplate(object sender, RoutedEventArgs e)
    {
        var t = PickedFrame();
        if (t == null) { Status = NoFramePicked("export"); return; }
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export this template as a shareable bundle",
                FileName = TextUtil.Slug(t.Name) + TemplateImporter.BundleExtension,
                Filter = "Template bundle|*" + TemplateImporter.BundleExtension + "|Zip archive|*.zip",
            };
            if (dlg.ShowDialog() != true) return;
            var dir = Path.GetDirectoryName(t.FramePath)!;
            TemplateImporter.ExportBundle(dir, dlg.FileName);
            Status = $"Exported \"{t.Name}\" to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Status = "Couldn't export template: " + ex.Message;
        }
    }

    /// <summary>Deletes the selected template's folder — but only for the user's own frames. Built-in and
    /// bundled frames are protected (they self-heal on launch, so deleting them is pointless).</summary>
    private void OnDeleteFrame(object sender, RoutedEventArgs e)
    {
        var t = PickedFrame();
        if (t == null) { Status = NoFramePicked("delete"); return; }

        var dir = Path.GetDirectoryName(t.FramePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Status = "That frame has no folder on disk to delete.";
            return;
        }
        var slug = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));

        if (TemplateService.IsBuiltIn(slug))
        {
            ConfirmDialog.Show(this, "Delete frame",
                $"“{t.Name}” is a built-in frame, so it can't be deleted — it would reappear on the next "
                + "launch. To make a version you can change or remove, open Design… and use “Save as new…”.",
                affirmative: "OK");
            return;
        }

        int inUse = TemplateService.CountReferencing(Cards, t.Name);
        var warn = inUse > 0
            ? $" {inUse} card(s) in this project use it and will fall back to the default frame "
              + "(they'll be flagged until you pick another)."
            : "";
        if (ConfirmDialog.Show(this, "Delete frame",
                $"Delete the frame “{t.Name}”? This removes it from your templates folder and can't be undone."
                + warn,
                affirmative: "Delete", cancel: "Cancel") != ConfirmResult.Affirmative)
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
            RefreshTemplates();
            Status = $"Deleted frame “{t.Name}”.";
        }
        catch (Exception ex)
        {
            Status = "Couldn't delete frame: " + ex.Message;
        }
    }

    /// <summary>Opens the frame-design editor for the selected template; regenerates + re-renders on Apply.</summary>
    private void OnFrameDesign(object sender, RoutedEventArgs e)
    {
        var t = PickedFrame();
        if (t == null) { Status = NoFramePicked("design"); return; }
        try
        {
            // Start from the frame as it is on disk, not the copy loaded with the frame list: an edit made since
            // (--editframe, another window, a script) would otherwise be overwritten by Apply.
            t = TemplateService.Reload(t) ?? t;
            var dir = Path.GetDirectoryName(t.FramePath);
            var slug = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            var win = new FrameDesignWindow(t, PreviewSide) { Owner = this, IsBuiltIn = TemplateService.IsBuiltIn(slug) };   // previews the card you're working on
            if (win.ShowDialog() == true)
            {
                // Renamed in place: cards find their frame by name, so every card (and the set's house frame)
                // that used the old name follows it — not just the selected one.
                int renamed = 0;
                if (win.RenamedFrom is { } oldName && win.AppliedTemplateName is { } newName && oldName != newName)
                {
                    renamed = RenameFrameReferences(oldName, newName);
                    if (renamed > 0) { MarkDirty(); CommitHistory(); }
                }
                RefreshTemplates(FrameToPutOnCard(t.Name, win.AppliedTemplateName, win.RenamedFrom));
                RenderPreview();
                Status = renamed > 0
                    ? $"Renamed the frame to \"{win.AppliedTemplateName}\" and moved {renamed} card(s) to it. Other saved sets still use \"{win.RenamedFrom}\"."
                    : $"Updated frame design for \"{win.AppliedTemplateName}\".";
            }
        }
        catch (Exception ex)
        {
            Status = "Frame design failed: " + ex.Message;
        }
    }

    /// <summary>After Frame Design: the frame to put on the selected card — only a new one (Save as new). A frame
    /// changed or renamed in place leaves every card on the frame it had (a renamed frame's cards follow it by
    /// name), including a card whose own frame isn't installed.</summary>
    internal static string? FrameToPutOnCard(string opened, string? applied, string? renamedFrom)
        => renamedFrom == null && applied != null && applied != opened ? applied : null;

    /// <summary>Points every card face/half and the set's house frame that use frame <paramref name="from"/> at
    /// <paramref name="to"/>. Returns how many references changed.</summary>
    internal int RenameFrameReferences(string from, string to)
    {
        int n = 0;
        void Fix(CardModel? c)
        {
            if (c == null) return;
            if (c.TemplateName == from) { c.TemplateName = to; n++; }
            if (c.HalfTemplateName == from) { c.HalfTemplateName = to; n++; }
        }
        foreach (var c in Cards) { Fix(c); Fix(c.BackFace); Fix(c.OtherHalf); }
        if (_defaultTemplate == from) { _defaultTemplate = to; _metaDirty = true; n++; }
        return n;
    }

    /// <summary>Reloads the template list from disk (e.g. after importing a new frame).</summary>
    /// <param name="applyName">A frame to put on the selected card (one just imported or designed); null keeps the
    /// card's own frame — even when that frame was just deleted, so the card is flagged instead of quietly moved.</param>
    private void RefreshTemplates(string? applyName = null)
    {
        var wanted = _selectedTemplate?.Name;
        Templates.Clear();
        foreach (var t in _templates.LoadAll()) Templates.Add(t);
        if (PreviewSide != null) SyncFramePicker();   // the shown face's own frame, blank if it's gone
        else
        {
            _syncingTemplates = true;
            try { SelectedTemplate = Templates.FirstOrDefault(t => t.Name == wanted) ?? Templates.FirstOrDefault(); }
            finally { _syncingTemplates = false; }
        }
        if (applyName != null && Templates.FirstOrDefault(t => t.Name == applyName) is { } applied)
            SelectedTemplate = applied;   // a user action: re-frames the card as one undo step
        RefreshHalfFrameBox();
    }

    private void OnMatchArtFolder(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dlg = new OpenFolderDialog { Title = "Choose a folder of art images to match by name" };
        if (dlg.ShowDialog() != true) return;
        Busy = true;
        try
        {
            var report = ArtMatcher.MatchIntoWithReport(Cards, dlg.FolderName, overwrite: false);
            _artBaseDir = dlg.FolderName;
            if (report.Total > 0) { _metaDirty = true; MarkDirty(); CommitHistory(); }
            RenderPreview();
            if (report.Total == 0)
            {
                Status = "No filename matches found.";
            }
            else
            {
                Status = $"Matched art for {report.Total} card(s)"
                         + (report.Fuzzy > 0 ? $" ({report.Fuzzy} by name guess — please review)" : "")
                         + ".";
                if (report.Fuzzy > 0)
                {
                    var names = string.Join("\n", report.Fuzzies.Take(15).Select(m =>
                        $"  • {m.Card.Name}  →  {Path.GetFileName(m.ArtPath)}"));
                    if (report.Fuzzy > 15) names += $"\n  …and {report.Fuzzy - 15} more";
                    ConfirmDialog.Show(this, "Review fuzzy art matches",
                        $"{report.Exact} card(s) matched exactly. {report.Fuzzy} were matched by a best-guess "
                        + "name match and may be wrong — please check these:\n\n" + names,
                        "OK");
                }
            }
        }
        catch (Exception ex)
        {
            Status = "Art match failed: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // --- single-card buttons ------------------------------------------------

    private async void OnLookup(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var card = _selectedCard;   // capture: a selection change mid-await must not write to a different card
        if (card == null) return;
        var query = card.Name;
        if (string.IsNullOrWhiteSpace(query)) { Status = "Type a card name first."; return; }

        Busy = true;
        Status = $"Looking up \"{query}\" on Scryfall…";
        try
        {
            var faces = await _scryfall.LookupFacesAsync(query);
            if (faces.Count == 0) { Status = $"No card found for \"{query}\"."; return; }

            var f = faces[0];
            // The card becomes the one looked up — the same as Edit details' lookup: its printed details replace the
            // card's (a 5/5 left over from another card would be wrong on Lightning Bolt); Undo puts them back. The
            // card's own art, flavor and frame stay; the starter card's sample art and flavor count as none.
            bool ownArt = !string.IsNullOrWhiteSpace(card.ArtPath) && !SampleAssets.IsBundled(card.ArtPath);
            card.Name = f.Name;
            CardDetailsFill.ApplyScryfall(card, f);
            SampleCards.ClearStarterLeftovers(card);

            // A genuinely two-sided card becomes ONE card with a back face (flip + indicator + a -back.png on
            // export), exactly as batch import does. A split/flip/aftermath card is printed on one side, so
            // its other half is added as its own card.
            foreach (var extra in CardDetailsFill.AttachFaces(card, faces))
            {
                extra.TemplateName = card.TemplateName;
                _setProfile.ApplyDefaults(extra);
                Cards.Add(extra);
            }

            // A meld part (Bruna): its back is the melded card (Brisela), fetched separately.
            bool melded = await CardDetailsFill.AttachMeldAsync(_scryfall, card, f);
            if (melded && card.BackFace is { } mb)
            {
                _ = _symbols.PrimeAsync(ManaText.SymbolTokens(mb.ManaCost, mb.RulesText));
                if (ReferenceEquals(card, _selectedCard)) _meldPartner = MeldPartner(card);
            }

            _ = _symbols.PrimeAsync(faces.SelectMany(x => ManaText.SymbolTokens(x.ManaCost, x.RulesText)));

            // Pull the real art from Scryfall unless the card has art of its own — that (and its frame) is never
            // replaced by a lookup. Remember whether it did, so the status line can honestly say what changed.
            bool hadArt = ownArt;
            bool gotArt = ownArt || await ReplaceArtFromScryfall(card, f.ArtUrl);
            if (gotArt && !hadArt) CardDetailsFill.SplitSharedArt(card);
            if (card.BackFace is { } back) await FillArtFromScryfall(back, back.ArtUrl);
            for (int i = 1; i < faces.Count; i++)
                if (Cards.Contains(faces[i])) await FillArtFromScryfall(faces[i], faces[i].ArtUrl);

            MarkDirty();
            CommitHistory();
            RenderPreview();
            // Lookup fills the text fields; the frame is always kept, and existing art is kept too.
            string artNote = hadArt
                ? " Your art and frame are unchanged."
                : gotArt ? " Added matching art; your frame is unchanged."
                         : " No art found; your frame is unchanged.";
            RefreshCardShape();
            Status = card.IsMeld
                ? $"Loaded “{f.Name}” from Scryfall as a meld card: its back is the {card.MeldHalf} half of “{card.BackFace!.Name}”."
                  + (MeldPartner(card) == null && card.MeldWith.Length > 0 ? $" Use “Add partner” for “{card.MeldWith}”." : "") + artNote
                : card.IsDoubleFaced
                ? $"Loaded “{f.Name}” from Scryfall as one double-faced card — click Back above the preview to see its back.{artNote}"
                : card.IsSplit
                ? $"Loaded “{f.Name} // {card.OtherHalf!.Name}” from Scryfall as one split card.{artNote}"
                : card.IsFlip
                ? $"Loaded “{f.Name}” from Scryfall as one flip card — click Flipped half above the preview to read it.{artNote}"
                : faces.Count > 1
                    ? $"Loaded “{f.Name}” details from Scryfall (+{faces.Count - 1} card for the other half).{artNote}"
                    : $"Loaded “{f.Name}” details from Scryfall.{artNote}";
        }
        catch (ScryfallException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = "Lookup failed: " + ex.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>Puts Scryfall's art on a card whose art isn't its own (none, or the starter's sample), framing reset.
    /// On a failed download the card keeps what it had. Returns true if art was set.</summary>
    private static async Task<bool> ReplaceArtFromScryfall(CardModel card, string artUrl)
    {
        if (string.IsNullOrWhiteSpace(artUrl)) return false;
        try
        {
            card.ArtPath = await ImageIntake.DownloadAsync(artUrl);
            card.ArtScale = 1.0;
            card.ArtOffsetX = 0;
            card.ArtOffsetY = 0;
            return true;
        }
        catch { return false; /* keep the card's art rather than failing the lookup */ }
    }

    /// <summary>Downloads Scryfall art for a card that has none yet. Returns true if art was set.</summary>
    private static async Task<bool> FillArtFromScryfall(CardModel card, string artUrl)
    {
        if (!string.IsNullOrWhiteSpace(card.ArtPath)) return true;
        if (string.IsNullOrWhiteSpace(artUrl)) return false;
        try { card.ArtPath = await ImageIntake.DownloadAsync(artUrl); return true; }
        catch { return false; /* keep the card without art rather than failing the lookup */ }
    }

    private void OnLoadArt(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose card art",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
            SetArt(dlg.FileName);
    }

    /// <summary>Points the selected card at a new art file and resets its pan/zoom. The image is copied
    /// into the portable art cache so the project stays self-contained if the source later moves.</summary>
    private void SetArt(string path) => SetArt(ActiveFace, path);

    /// <summary>Points a specific face at a new art file and resets its pan/zoom. Callers that await
    /// anything first MUST capture the face and pass it here: the user can select another card (or flip to
    /// the other side) mid-download, and re-reading ActiveFace would overwrite THAT face's art instead.</summary>
    private void SetArt(CardModel? card, string path)
    {
        if (card == null) return;
        if (!InProject(card))
        {
            // Undo (or a delete) replaced the cards while the art downloaded: this one isn't in the set any more.
            Status = "That card was undone or removed before its art arrived, so the art wasn't added.";
            return;
        }
        var local = ImageIntake.EnsureLocalCopy(path);
        card.ArtPath = local;
        card.ArtScale = 1.0;
        card.ArtOffsetX = 0;
        card.ArtOffsetY = 0;
        // Only the selected card's edits reach OnCardChanged; art landing on a card the user has since clicked
        // away from must still mark the set unsaved and be an undo step.
        MarkDirty();
        CommitHistory();
        Status = "Loaded art: " + Path.GetFileName(local)
                 + (card.IsBackFace ? " (back face)" : card.IsOtherHalf ? $" (for “{card.Name}”)" : "");
    }

    /// <summary>Whether a card (or a back face / other half) is still part of the set.</summary>
    private bool InProject(CardModel card) =>
        Cards.Any(c => ReferenceEquals(c, card) || ReferenceEquals(c.BackFace, card) || ReferenceEquals(c.OtherHalf, card));

    private void OnResetArt(object sender, RoutedEventArgs e) => ResetArt();

    /// <summary>Puts the art back where it started — centred, at its original size — undoing any pan and zoom
    /// (the Reset button, or 0 on the focused preview). Undoable like any other edit.</summary>
    private void ResetArt()
    {
        var card = ActiveFace;
        if (card == null) return;
        if (card.ArtScale == 1.0 && card.ArtOffsetX == 0 && card.ArtOffsetY == 0)
        {
            Status = "The art is already centred at its original size.";
            return;
        }
        card.ArtScale = 1.0;
        card.ArtOffsetX = 0;
        card.ArtOffsetY = 0;
        RenderPreview();
        Status = "Art reset: centred at its original size" + (card.IsBackFace ? " (back face)." : ".");
    }

    /// <summary>While the preview has keyboard focus, the hint under it says so — the keys only work then.</summary>
    private void OnPreviewFocusChanged(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        => PreviewHint.Text = PreviewImageControl.IsKeyboardFocusWithin
            ? "Keys on: arrows nudge the art (Shift = 10×) · + / − zoom · 0 resets · click elsewhere to stop"
            : "Drag to pan · scroll to zoom · click the card for keys: arrows nudge, + / − zoom, 0 resets";

    private void OnClearArt(object sender, RoutedEventArgs e)
    {
        var card = ActiveFace;
        if (card == null) return;
        card.ArtPath = "";
        card.ArtScale = 1.0;   // reset framing too, matching Change art… (so stale pan/zoom isn't reused)
        card.ArtOffsetX = 0;
        card.ArtOffsetY = 0;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Busy) { Status = "Busy — wait for the current export to finish."; return; }
        if (_selectedCard == null) return;
        var template = TemplateFor(_selectedCard);   // the front's own frame, whichever side is shown
        if (template == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export card",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = SafeName(_selectedCard.Name) + ".png",
            InitialDirectory = DefaultOutputDir(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bmp = CardExporter.AtCardSize(_renderer.RenderToBitmap(_selectedCard, template, supersample: 2));
            CardExporter.Save(bmp, dlg.FileName);   // PNG or JPEG by extension
            _lastExportDir = Path.GetDirectoryName(dlg.FileName);   // so "Open output folder" opens here

            // Double-faced: write the back alongside as "<name>-back<ext>".
            if (_selectedCard.BackFace is { } back)
            {
                var backPath = BackFacePath(dlg.FileName);
                CardExporter.Save(CardExporter.AtCardSize(_renderer.RenderToBitmap(back, TemplateFor(back) ?? template, supersample: 2)), backPath);
                Status = $"Exported {Path.GetFileName(dlg.FileName)} + {Path.GetFileName(backPath)} (front & back).";
            }
            else
            {
                Status = $"Exported {Path.GetFileName(dlg.FileName)} ({bmp.PixelWidth}x{bmp.PixelHeight}).";
            }
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    /// <summary>Turns "…/Name.png" into "…/Name-back.png" for a double-faced card's second side.</summary>
    private static string BackFacePath(string frontPath)
    {
        var dir = Path.GetDirectoryName(frontPath) ?? "";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(frontPath) + "-back" + Path.GetExtension(frontPath));
    }

    // --- drag-to-pan / wheel-zoom art on the preview ------------------------

    private bool _isPanning;
    private System.Windows.Point _panStart;
    private CardRenderer.ArtDrag? _artDrag;   // the live drag/zoom: everything but the art, drawn once
    private bool _liveQueued;

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PreviewImageControl.Focus();   // so the arrow keys nudge the art after you click the preview
        PickSplitHalfAt(e.GetPosition(PreviewImageControl));   // a split card: the clicked half takes the art
        if (ActiveFace is not { ArtPath: { Length: > 0 } }) return;
        _isPanning = true;
        _panStart = e.GetPosition(PreviewImageControl);
        PreviewImageControl.CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var card = ActiveFace;
        if (!_isPanning || card == null) return;
        var p = e.GetPosition(PreviewImageControl);
        var (w, h) = DisplayedCardSize();
        if (w <= 0 || h <= 0) return;

        var (ax, ay) = PanDelta((p.X - _panStart.X) / w, (p.Y - _panStart.Y) / h);
        card.ArtOffsetX = Clamp(card.ArtOffsetX + ax, -0.5, 0.5);
        card.ArtOffsetY = Clamp(card.ArtOffsetY + ay, -0.5, 0.5);
        _panStart = p;
        RenderPreviewLive();   // live feedback while dragging (the debounce timer alone never fires mid-drag)
    }

    private void OnPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isPanning = false;
        PreviewImageControl.ReleaseMouseCapture();
        RenderPreview();   // final crisp render
    }

    private void OnPreviewWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var card = ActiveFace;
        if (card is not { ArtPath: { Length: > 0 } }) return;
        // Finer by default; Ctrl = ultra-fine, Shift = coarse. (Was a fixed 0.08/notch — too jumpy.)
        var mods = System.Windows.Input.Keyboard.Modifiers;
        double per = mods.HasFlag(System.Windows.Input.ModifierKeys.Control) ? 0.01
                   : mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 0.08
                   : 0.03;
        card.ArtScale = Clamp(card.ArtScale + e.Delta / 120.0 * per, 0.5, 3.0);
        RenderPreviewLive();
    }

    /// <summary>Nudges the art by a fraction of the card (dx/dy in 0..1 card-space). Used by the arrow keys.</summary>
    private void NudgeArt(double dx, double dy)
    {
        var card = ActiveFace;
        if (card == null) return;
        var (ax, ay) = PanDelta(dx, dy);
        card.ArtOffsetX = Clamp(card.ArtOffsetX + ax, -0.5, 0.5);
        card.ArtOffsetY = Clamp(card.ArtOffsetY + ay, -0.5, 0.5);
        RenderPreviewLive();   // a held arrow key repeats fast; the full render follows when it stops
    }

    /// <summary>Zooms the art by a small step (used by the +/- keys).</summary>
    private void ZoomArt(double delta)
    {
        var card = ActiveFace;
        if (card == null) return;
        card.ArtScale = Clamp(card.ArtScale + delta, 0.5, 3.0);
        RenderPreviewLive();
    }

    /// <summary>
    /// Shows the art where it is now, at most once per screen refresh however fast the mouse or a key repeats. The
    /// card's frame, text and symbols are drawn once (<see cref="CardRenderer.BeginArtDrag"/>); each move after that
    /// only moves the art under them, put together by the graphics card. A card drawn some other way (a split card,
    /// a meld back, the whole melded card) renders in full instead, still once per refresh.
    /// </summary>
    private void RenderPreviewLive()
    {
        if (_liveQueued) return;
        _liveQueued = true;
        System.Windows.Media.CompositionTarget.Rendering += OnLiveFrame;
    }

    private void OnLiveFrame(object? sender, EventArgs e)
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnLiveFrame;
        if (!_liveQueued) return;   // a full render came first
        _liveQueued = false;
        var card = PreviewSide;
        var template = card == null ? null : TemplateFor(card);
        if (card == null || template == null || _viewingMelded || (_readingSideways && card.IsSplit))
        {
            RenderPreview(inspect: false);
            return;
        }
        if (_artDrag == null || !ReferenceEquals(_artDrag.Card, card) || _artDrag.Template.Name != template.Name
            || _artDrag.Template.FramePath != template.FramePath)
            _artDrag = _renderer.BeginArtDrag(card, template);
        if (_artDrag == null)
        {
            RenderPreview(inspect: false);
            return;
        }
        var drawing = _renderer.DrawArtDrag(_artDrag);
        if (_viewingFlipped && card.IsFlip)
        {
            var b = drawing.Bounds;   // the same half-turn the full preview gets
            var turned = new System.Windows.Media.DrawingGroup { Transform = new System.Windows.Media.RotateTransform(180, b.Width / 2, b.Height / 2) };
            turned.Children.Add(drawing);
            drawing = turned;
        }
        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();
        LiveArtImage.Source = image;
        LiveArtImage.Visibility = Visibility.Visible;
    }

    /// <summary>Back to the rendered preview: drops the live drawing (and its overlay, which may be out of date now).</summary>
    private void EndLiveArt()
    {
        _liveQueued = false;
        _artDrag = null;
        if (LiveArtImage.Visibility != Visibility.Collapsed)
        {
            LiveArtImage.Visibility = Visibility.Collapsed;
            LiveArtImage.Source = null;
        }
    }

    /// <summary>Size of the letterboxed card inside the preview Image — at the card's own aspect, which is
    /// 7:5 for a landscape Battle/Plane, not the usual 5:7.</summary>
    private (double w, double h) DisplayedCardSize()
    {
        double ew = PreviewImageControl.ActualWidth, eh = PreviewImageControl.ActualHeight;
        if (ew <= 0 || eh <= 0) return (0, 0);
        double aspect = PreviewImage is { PixelWidth: > 0, PixelHeight: > 0 } img
            ? (double)img.PixelHeight / img.PixelWidth
            : 1050.0 / 750.0;
        double h = Math.Min(eh, ew * aspect);
        return (h / aspect, h);
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>Once, when the window first shows: the built-in frames this version brought up to date, and where
    /// their old copies are — a copy someone changed by hand in an older version is replaced too, so they can get
    /// it back.</summary>
    internal void TellAboutReplacedFrames()
    {
        List<(string Name, string Dir, string Kept)> replaced;
        lock (TemplateService.ReplacedBuiltIns)
        {
            replaced = TemplateService.ReplacedBuiltIns.ToList();
            TemplateService.ReplacedBuiltIns.Clear();
        }
        if (replaced.Count == 0) return;
        var names = string.Join(", ", replaced.Select(r => r.Name).Distinct());
        var answer = ConfirmDialog.Show(this, "Built-in frames updated",
            $"This version brought these built-in frames up to date: {names}.\n\n"
            + "If you had changed any of them yourself in an older version, put your copies back — they'll stay "
            + "yours from now on. Otherwise keep the new ones (they fix how text sits on the frame).\n\n"
            + "The old copies are kept in " + TemplateService.ReplacedFramesDir + " either way.",
            "Keep the new ones", "Put my copies back");
        if (answer != ConfirmResult.Negative)
        {
            Status = $"Brought {replaced.Count} built-in frame(s) up to date; the old copies are in {TemplateService.ReplacedFramesDir}.";
            return;
        }
        try
        {
            foreach (var r in replaced) TemplateService.RestoreReplaced(r.Dir, r.Kept);
            RefreshTemplates();
            RenderPreview();
            Status = $"Put back your copies of {names}.";
        }
        catch (Exception ex) { Status = "Couldn't put the frames back: " + ex.Message; }
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        // Open where you last exported (e.g. C:\temp\bob), else the set's \out folder, else the shared output dir.
        try { Process.Start("explorer.exe", DefaultOutputDir()); }
        catch (Exception ex) { Status = "Couldn't open folder: " + ex.Message; }
    }

    private static string SafeName(string name) => TextUtil.SafeFileName(name);

    // --- paste / drag-drop / clipboard art ----------------------------------

    private async void OnPasteArt(object sender, RoutedEventArgs e) => await PasteArtAsync();

    /// <summary>Pastes art from the clipboard: a bitmap, a copied image file, or an image URL.</summary>
    private async Task PasteArtAsync()
    {
        var face = ActiveFace;   // capture: selecting another card / flipping mid-download must not retarget
        if (face == null) return;
        try
        {
            // A real bitmap on the clipboard (e.g. a browser's "Copy image"). Read it robustly: the plain
            // Clipboard.GetImage() yields an all-black image for browser DIBs whose alpha channel is zeroed.
            var bmp = TryGetClipboardImage();
            if (bmp != null) { SetArt(face, ImageIntake.SaveBitmap(bmp)); return; }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var file = System.Windows.Clipboard.GetFileDropList().Cast<string>()
                    .FirstOrDefault(ImageIntake.LooksLikeImagePath);
                if (file != null) { SetArt(face, file); return; }
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (ImageIntake.IsHttpUrl(text)) { await DownloadArtAsync(text, face); return; }
            }
            Status = "Clipboard has no image, image file, or image URL to paste.";
        }
        catch (Exception ex)
        {
            Status = "Paste art failed: " + ex.Message;
        }
    }

    /// <summary>Reads an image off the clipboard robustly. Prefers the "PNG" format that browsers provide
    /// (correct colors + alpha); otherwise falls back to the DIB bitmap, repairing the all-zero alpha channel
    /// that makes a browser-copied image paste as solid black. Returns null if there's no usable image.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? TryGetClipboardImage()
    {
        System.Windows.IDataObject? data;
        try { data = System.Windows.Clipboard.GetDataObject(); } catch { data = null; }
        if (data != null)
        {
            foreach (var fmt in new[] { "PNG", "image/png" })
            {
                try
                {
                    if (data.GetDataPresent(fmt) && data.GetData(fmt) is System.IO.Stream s && s.Length > 0)
                    {
                        s.Position = 0;
                        var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(
                            s, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        var frame = dec.Frames[0];
                        frame.Freeze();
                        return frame;
                    }
                }
                catch { /* try the next format, then the DIB fallback */ }
            }
        }

        if (!System.Windows.Clipboard.ContainsImage()) return null;
        var img = System.Windows.Clipboard.GetImage();
        return img == null ? null : ImageIntake.RepairZeroAlpha(img);
    }

    private async Task DownloadArtAsync(string url, CardModel? target = null)
    {
        var face = target ?? ActiveFace;   // captured BEFORE the download; see SetArt(CardModel?, string)
        Status = "Downloading art…";
        try
        {
            var path = await ImageIntake.DownloadAsync(url);
            SetArt(face, path);
        }
        catch (Exception ex)
        {
            Status = "Couldn't download art: " + ex.Message;
        }
    }

    private void OnPreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (Busy) return;   // don't accept a drop mid-import/lookup/export
        try
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
                return;
            await DropFiles(files, e.GetPosition(PreviewImageControl));
        }
        catch (Exception ex)
        {
            Status = "Couldn't handle that drop: " + ex.Message;
        }
    }

    /// <summary>What a drop does: a project opens, a card list imports, a frame bundle installs, and an image becomes
    /// the art of the card (or the split card's half) it was dropped on. <paramref name="at"/> is on the preview.</summary>
    private async Task DropFiles(string[] files, System.Windows.Point at)
    {
        // Mid-import or mid-export, a drop would swap the cards out from under the running job.
        if (Busy) { Status = "Still working — wait for the current job to finish."; return; }
        // A dropped project/list opens or imports; a dropped image becomes the current card's art.
        var project = files.FirstOrDefault(f =>
        {
            var x = Path.GetExtension(f).ToLowerInvariant();
            return x is ".cardinator" or ".json";
        });
        if (project != null) { if (ConfirmDiscardIfDirty()) LoadProjectFile(project); return; }

        var list = files.FirstOrDefault(f =>
        {
            var x = Path.GetExtension(f).ToLowerInvariant();
            return x is ".txt" or ".csv" or ".tsv";
        });
        if (list != null) { await ImportListFile(list); return; }

        // A shared set opens; a shared frame (.cardframe, or a .zip of frames) installs, as Import frame… would.
        var bundle = files.FirstOrDefault(TemplateImporter.IsBundlePath);
        if (bundle != null && SetPackager.IsSharedSet(bundle))
        {
            if (ConfirmDiscardIfDirty()) OpenSharedSet(bundle);
            return;
        }
        if (bundle != null)
        {
            try { ImportFrameBundle(bundle); }
            catch (Exception ex) { Status = "Couldn't import the frame: " + ex.Message; }
            return;
        }

        var image = files.FirstOrDefault(ImageIntake.LooksLikeImagePath);
        if (image == null) { Status = "That file type isn't an image, frame, project, or card list."; return; }
        if (_viewingCardBack) { Status = "That's the card back — pick Front, then drop the art."; return; }
        if (ActiveFace == null) { Status = "Select a card first, then drop art onto it."; return; }
        PickSplitHalfAt(at);   // dropped onto a split card's half: that half
        SetArt(image);
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        var card = PreviewSide;
        if (card == null) return;
        var template = TemplateFor(card);
        if (template == null) return;
        try
        {
            var bmp = _renderer.RenderToBitmap(card, template, supersample: 2);
            if (bmp.CanFreeze) bmp.Freeze();
            SetClipboardImageWithRetry(bmp);
            Status = "Copied the card image to the clipboard.";
        }
        catch (Exception ex)
        {
            Status = "Copy failed: " + ex.Message;
        }
    }

    /// <summary>
    /// The Windows clipboard is a shared resource another app may briefly hold open, so
    /// SetImage can fail transiently with a COM error. Retry a few times before giving up.
    /// </summary>
    private static void SetClipboardImageWithRetry(BitmapSource bmp)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var data = new System.Windows.DataObject();
                data.SetImage(bmp);
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    // --- keyboard shortcuts & unsaved-changes guard -------------------------

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            OnHelp(sender, e);
            e.Handled = true;
            return;
        }
        var mods = System.Windows.Input.Keyboard.Modifiers;

        // Nudge/zoom the art with the keyboard — but only when the preview is focused (click it first),
        // so arrow keys still navigate the card list and move the caret in text fields.
        if (PreviewImageControl.IsKeyboardFocusWithin
            && ActiveFace is { ArtPath: { Length: > 0 } })
        {
            bool shift = mods.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            var canvas = TemplateFor(ActiveFace!)?.Spec;
            double cw = canvas?.CanvasWidth ?? 750, ch = canvas?.CanvasHeight ?? 1050;
            double sx = (shift ? 10.0 : 1.0) / cw, sy = (shift ? 10.0 : 1.0) / ch;   // 1px (or 10px) of the card
            double zstep = shift ? 0.10 : 0.02;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Left:  NudgeArt(-sx, 0); e.Handled = true; return;
                case System.Windows.Input.Key.Right: NudgeArt(+sx, 0); e.Handled = true; return;
                case System.Windows.Input.Key.Up:    NudgeArt(0, -sy); e.Handled = true; return;
                case System.Windows.Input.Key.Down:  NudgeArt(0, +sy); e.Handled = true; return;
                case System.Windows.Input.Key.OemPlus:
                case System.Windows.Input.Key.Add:   ZoomArt(+zstep); e.Handled = true; return;
                case System.Windows.Input.Key.OemMinus:
                case System.Windows.Input.Key.Subtract: ZoomArt(-zstep); e.Handled = true; return;
                case System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0
                    when mods == System.Windows.Input.ModifierKeys.None:
                    ResetArt(); e.Handled = true; return;
            }
        }

        // Ctrl+Shift+Z is a common "redo" alias.
        if (mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)
            && e.Key == System.Windows.Input.Key.Z)
        {
            Redo(); e.Handled = true; return;
        }
        if (mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)
            && e.Key == System.Windows.Input.Key.S)
        {
            OnSaveProjectAs(sender, e); e.Handled = true; return;
        }
        if (mods != System.Windows.Input.ModifierKeys.Control) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.N: OnNewProject(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.O: OnOpenProject(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.S: OnSaveProject(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.E: OnExport(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.L: OnLookup(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.D: OnDuplicateCard(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.Z: Undo(); e.Handled = true; break;
            case System.Windows.Input.Key.Y: Redo(); e.Handled = true; break;
        }
    }

    /// <summary>Set for off-screen/headless rendering so closing never shows the save prompt.</summary>
    public bool SuppressClosePrompt { get; set; }

    /// <summary>The fewest pixels the card list keeps (about four cards): below that the BATCH buttons scroll instead.</summary>
    internal const double MinCardListHeight = 150;

    /// <summary>Caps the BATCH buttons' height to what's left after the buttons above the card list and the list's
    /// minimum — on a short window (or a display scaled to 150%) they took it all, and the list showed one card, cut off.</summary>
    private void OnSidebarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double top = 0;
        foreach (UIElement child in Sidebar.Children)
            if (child is FrameworkElement fe && System.Windows.Controls.DockPanel.GetDock(fe) == System.Windows.Controls.Dock.Top && !ReferenceEquals(fe, BatchScroll))
                top += fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
        BatchScroll.MaxHeight = Math.Max(60, Sidebar.ActualHeight - top - MinCardListHeight);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (SuppressClosePrompt) return;
        // An export or print sheet still writing would be cut off mid-file when the app exits.
        if (IsExporting && ConfirmDialog.Show(this, "Export still running",
                "Cards are still being exported. Closing now stops it part-way, and the file being written may be "
                + "left incomplete.", affirmative: "Keep exporting", negative: "Close anyway") != ConfirmResult.Negative)
        {
            e.Cancel = true;
            return;
        }
        // A lookup or import part-way through: what it has filled in so far isn't marked unsaved until it's done, so
        // the unsaved-changes question below wouldn't come up — it was lost without a word.
        if (Busy && ConfirmDialog.Show(this, "Still working",
                "A lookup or import is still running. Closing now stops it, and the cards it has filled in so far are lost.",
                affirmative: "Keep working", negative: "Close anyway") != ConfirmResult.Negative)
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscardIfDirty()) e.Cancel = true;
        else { _recoveryTimer.Stop(); ClearRecovery(); }
    }

    // --- INotifyPropertyChanged ---------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
