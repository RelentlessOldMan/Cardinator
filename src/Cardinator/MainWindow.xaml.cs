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
    private string _projectName = "Untitled Project";
    private string _artBaseDir = "";
    private string _projectPath = "";     // last saved/opened file, so Ctrl+S can re-save silently
    private string? _projectFolder;       // the set folder (holds the project file + art/ + out/); null until saved/opened
    private string? _lastExportDir;       // where the last PNG/sheet went, so "Open output folder" opens there
    private string _defaultTemplate = ""; // the set's saved house frame, inherited by new/imported cards
    private SetProfile _setProfile = new();  // the set's shared metadata defaults (W1), inherited by new/imported cards
    private bool _viewingBack;                // DFC: preview is showing the back face (preview-only flip)
    private bool _viewingFlipped;             // flip card: preview is turned upside down to read the other half
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
    private DispatcherTimer _undoTimer = null!;
    private bool _restoring;
    private static readonly System.Text.Json.JsonSerializerOptions SnapOpts =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public MainWindow()
    {
        _renderer = new CardRenderer(_symbols);
        InitializeComponent();
        DataContext = this;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderPreview(); };

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); CommitHistory(); };

        Templates = new(_templates.LoadAll());
        _selectedTemplate = Templates.FirstOrDefault();

        Cards = new();
        Cards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(HasCards));
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

        DfcStyleBox.ItemsSource = DfcStyleOptions;
        RefreshDfcControls();
        RefreshFlipControls();

        // Download authentic Scryfall symbols in the background; re-render as they arrive.
        _symbols.Updated += OnSymbolsUpdated;
        _ = _symbols.PrimeAsync();
    }

    // --- double-faced cards (DFC) -------------------------------------------

    /// <summary>Display label ↔ stored DfcStyle value for the indicator dropdown.</summary>
    private sealed record DfcStyleOption(string Label, string Value) { public override string ToString() => Label; }
    private static readonly DfcStyleOption[] DfcStyleOptions =
    {
        new("No indicator", "none"),
        new("Flip arrow", "arrow"),
        new("Sun / moon", "sunmoon"),
    };

    private bool _syncingDfcControls;

    /// <summary>Syncs the DFC buttons + style dropdown to the selected card's state.</summary>
    private void RefreshDfcControls()
    {
        _syncingDfcControls = true;
        try
        {
            bool dfc = _selectedCard?.IsDoubleFaced == true;
            DfcToggleBtn.Content = dfc ? "Remove back face" : "Make double-faced";
            DfcToggleBtn.IsEnabled = dfc || _selectedCard?.OtherHalf == null;   // a flip card can't also have a back
            DfcEditBackBtn.IsEnabled = dfc;
            DfcFlipBtn.IsEnabled = dfc;
            DfcStyleBox.IsEnabled = dfc;   // the indicator only draws on a double-faced card
            DfcFlipBtn.Content = _viewingBack ? "Show front" : "Show back";
            var style = (_selectedCard?.DfcStyle ?? "").Trim().ToLowerInvariant();
            if (style.Length == 0) style = "none";
            DfcStyleBox.SelectedItem = DfcStyleOptions.FirstOrDefault(o => o.Value == style) ?? DfcStyleOptions[0];
        }
        finally { _syncingDfcControls = false; }
    }

    private void OnToggleDfc(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        if (_selectedCard.IsDoubleFaced)
        {
            if (ConfirmDialog.Show(this, "Remove back face",
                    $"Remove the back face “{_selectedCard.BackFace!.Name}”? This can't be undone except with Undo.",
                    affirmative: "Remove", cancel: "Cancel") != ConfirmResult.Affirmative)
                return;
            _selectedCard.BackFace = null;
            _viewingBack = false;
            Status = "Removed the back face.";
        }
        else
        {
            // New blank back face inheriting the front's frame + set defaults, so it's ready to edit.
            var back = new CardModel { TemplateName = _selectedCard.TemplateName };
            _setProfile.ApplyDefaults(back);
            if (string.IsNullOrWhiteSpace(_selectedCard.DfcStyle)) _selectedCard.DfcStyle = "sunmoon";   // a sensible default glyph
            _selectedCard.BackFace = back;
            Status = "Added a back face — use “Edit back face…”. Click “Show back” to preview it.";
        }
        MarkDirty();
        CommitHistory();
        RefreshDfcControls();
        RefreshFlipControls();   // a double-faced card can't also be a flip card
        RenderPreview();
    }

    private void OnEditBackFace(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.BackFace == null) return;
        new DetailsWindow(_selectedCard.BackFace, _scryfall) { Owner = this }.ShowDialog();
        // No MarkDirty here: Cancel restores the snapshot, so an unchanged card must stay clean.
        // CommitHistory recomputes Dirty from the history position, which is accurate either way.
        CommitHistory();
        RenderPreview();
    }

    private void OnFlipPreview(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.BackFace == null) return;
        _viewingBack = !_viewingBack;
        RefreshDfcControls();
        RenderPreview();
    }

    private void OnDfcStyleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingDfcControls || _selectedCard == null) return;
        if (DfcStyleBox.SelectedItem is DfcStyleOption opt && _selectedCard.DfcStyle != opt.Value)
        {
            _selectedCard.DfcStyle = opt.Value;   // setter also syncs the back face
            MarkDirty();
            QueueUndoCommit();
            RenderPreview();
        }
    }

    // --- flip cards (two-part, one side) ------------------------------------

    /// <summary>Syncs the flip-card buttons to the selected card's state.</summary>
    private void RefreshFlipControls()
    {
        bool flip = _selectedCard?.OtherHalf != null;
        FlipToggleBtn.Content = flip ? "Remove flipped half" : "Make flip card";
        FlipToggleBtn.IsEnabled = _selectedCard != null && (flip || !_selectedCard.IsDoubleFaced);   // not both
        FlipEditBtn.IsEnabled = flip;
        FlipShowBtn.IsEnabled = flip;
        FlipShowBtn.Content = _viewingFlipped ? "Show upright" : "Show flipped";
    }

    private void OnToggleFlip(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        if (_selectedCard.OtherHalf != null)
        {
            if (ConfirmDialog.Show(this, "Remove flipped half",
                    $"Remove the flipped half “{_selectedCard.OtherHalf.Name}”? This can't be undone except with Undo.",
                    affirmative: "Remove", cancel: "Cancel") != ConfirmResult.Affirmative)
                return;
            _selectedCard.OtherHalf = null;
            _selectedCard.HalfLayout = "";
            _viewingFlipped = false;
            Status = "Removed the flipped half.";
        }
        else
        {
            if (_selectedCard.IsDoubleFaced) return;   // the button is disabled for these; belt and braces
            _selectedCard.HalfLayout = "flip";
            _selectedCard.OtherHalf = new CardModel { TemplateName = _selectedCard.TemplateName };
            Status = "Made a flip card — use “Edit flipped half…” for the upside-down half.";
        }
        MarkDirty();
        CommitHistory();
        RefreshFlipControls();
        RefreshDfcControls();
        RenderPreview();
    }

    private void OnEditFlippedHalf(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.OtherHalf == null) return;
        new DetailsWindow(_selectedCard.OtherHalf, _scryfall) { Owner = this }.ShowDialog();
        CommitHistory();   // recomputes Dirty from history, so a cancelled edit stays clean
        RenderPreview();
    }

    private void OnShowFlipped(object sender, RoutedEventArgs e)
    {
        if (_selectedCard?.OtherHalf == null) return;
        _viewingFlipped = !_viewingFlipped;
        RefreshFlipControls();
        RenderPreview();
    }

    /// <summary>Pan direction on the preview: reversed while it's shown upside down, so the art still moves
    /// the way the mouse does.</summary>
    private double PanSign => _viewingFlipped && _selectedCard?.IsFlip == true ? -1 : 1;

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
            if (_selectedCard != null)
            {
                AttachCardEvents(_selectedCard);
                var t = Templates.FirstOrDefault(x => x.Name == _selectedCard.TemplateName);
                if (t != null) { _selectedTemplate = t; OnPropertyChanged(nameof(SelectedTemplate)); }
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanEditSelected));
            RefreshDfcControls();
            RefreshFlipControls();
            RenderPreview();
        }
    }

    public bool HasSelection => _selectedCard != null;

    /// <summary>The face the preview currently shows — the back when flipped (DFC), otherwise the front.</summary>
    private CardModel? ActiveFace =>
        _viewingBack && _selectedCard?.BackFace != null ? _selectedCard.BackFace : _selectedCard;

    /// <summary>The template a given face ACTUALLY renders with: its own frame (falling back to the current one),
    /// turned to its landscape layout when the face is a Battle/Plane. Resolving here means the CHECKS panel
    /// and the pixel inspector judge the same geometry the preview draws.</summary>
    private Template? TemplateFor(CardModel card)
    {
        var t = Templates.FirstOrDefault(x => x.Name == card.TemplateName) ?? _selectedTemplate ?? Templates.FirstOrDefault();
        return t == null ? null : TemplateService.ResolveFor(card, t);
    }

    /// <summary>Short app version shown in the header (e.g. "v1.1.4"), so the running build is obvious.</summary>
    public string AppVersion => "v" + App.VersionNumber();

    /// <summary>True when the project has at least one card (gates batch/export actions).</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>A long/mutating operation (lookup, import, search, export…) is running. While true, other
    /// actions are disabled and re-entrant handlers early-return, so nothing conflicts or double-fires.</summary>
    public bool Busy
    {
        get => _busy;
        internal set   // internal: tests drive the busy state to check the guards
        {
            _busy = value;
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
            // programmatic re-sync (which would spuriously dirty the card / churn undo history).
            if (value != null && _selectedCard != null && !_restoring && !_loading
                && !string.Equals(value.Name, _selectedCard.TemplateName, StringComparison.Ordinal))
                _selectedCard.TemplateName = value.Name;
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
        MarkDirty();
        _renderTimer.Stop();
        _renderTimer.Start();   // debounce rapid edits (typing, slider drags)
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
        UpdateUndoRedo();
    }

    /// <summary>Marks the current history position as the saved baseline (called after Save/Open).</summary>
    private void MarkSavedPoint()
    {
        _savedHistIdx = _histIdx;
        Dirty = false;
    }

    private int SelectedIndex() => _selectedCard != null ? Cards.IndexOf(_selectedCard) : -1;
    private string SnapshotJson() => System.Text.Json.JsonSerializer.Serialize(Cards.ToList(), SnapOpts);

    /// <summary>Restart the debounce so a burst of edits becomes a single undo step.</summary>
    private void QueueUndoCommit()
    {
        if (_restoring || _loading) return;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>Capture the current project state as a new history entry (no-op if nothing changed).</summary>
    internal void CommitHistory()
    {
        if (_restoring || _loading) return;
        _undoTimer.Stop();
        var json = SnapshotJson();
        int sel = SelectedIndex();
        if (_histIdx >= 0 && _history[_histIdx].json == json && _history[_histIdx].sel == sel)
        {
            Dirty = _histIdx != _savedHistIdx;   // nothing changed — re-derive, so a cancelled edit clears ●
            return;
        }
        if (_histIdx < _history.Count - 1)
            _history.RemoveRange(_histIdx + 1, _history.Count - _histIdx - 1);   // drop the redo tail
        _history.Add((json, sel));
        _histIdx = _history.Count - 1;
        const int cap = 60;
        while (_history.Count > cap) { _history.RemoveAt(0); _histIdx--; _savedHistIdx--; }
        Dirty = _histIdx != _savedHistIdx;
        UpdateUndoRedo();
    }

    private void RestoreSnapshot((string json, int sel) snap)
    {
        _restoring = true;
        try
        {
            var cards = System.Text.Json.JsonSerializer.Deserialize<List<CardModel>>(snap.json, SnapOpts) ?? new();
            DetachCardEvents(_selectedCard);
            Cards.Clear();
            foreach (var c in cards) Cards.Add(c);
            _selectedCard = null;
            SelectedCard = (snap.sel >= 0 && snap.sel < Cards.Count) ? Cards[snap.sel] : Cards.FirstOrDefault();
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CanExport));
        // Dirty exactly when we're not sitting on the saved baseline (undo back to saved clears the ●).
        Dirty = _histIdx != _savedHistIdx;
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

    /// <summary>Sets the same set code / artist / rarity / copyright / frame on all cards — or, when a subset
    /// is multi-selected, just those (M14).</summary>
    private void OnBulkEdit(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) { Status = "No cards to edit."; return; }

        // M14: when a subset is multi-selected, offer to target just those instead of the whole set.
        IReadOnlyList<CardModel> targets = Cards.ToList();
        var selected = CardList.SelectedItems.Cast<CardModel>().ToList();
        if (selected.Count > 1 && selected.Count < Cards.Count)
        {
            var choice = ConfirmDialog.Show(this, "Set fields",
                $"Apply to the {selected.Count} selected card(s), or to all {Cards.Count}?",
                affirmative: $"Selected ({selected.Count})", negative: $"All ({Cards.Count})", cancel: "Cancel");
            if (choice == ConfirmResult.Cancel) return;
            if (choice == ConfirmResult.Affirmative) targets = selected;
        }

        var dlg = new BulkEditWindow(Templates.Select(t => t.Name)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        int n = ApplyBulkEdit(targets, dlg.SetCode, dlg.Artist, dlg.Rarity, dlg.Copyright, dlg.TemplateName, dlg.SetSymbolPath);
        Status = $"Applied changes to {n} card(s).";
    }

    /// <summary>W1: edit the set's house defaults (inherited by new/imported cards), optionally applying them
    /// to existing cards too. Unlike "Set fields on all", these persist on the project.</summary>
    private void OnSetDefaults(object sender, RoutedEventArgs e)
    {
        var dlg = new SetDefaultsWindow(_setProfile, _defaultTemplate, Templates.Select(t => t.Name)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _setProfile = dlg.Profile;
        _defaultTemplate = dlg.DefaultFrame;   // "" clears the house frame
        MarkDirty();

        if (dlg.ApplyToExisting && Cards.Count > 0)
        {
            int changed = Cards.Count(c => _setProfile.ApplyDefaults(c));
            // The default frame fills cards that don't already have one.
            if (!string.IsNullOrWhiteSpace(_defaultTemplate))
                foreach (var c in Cards.Where(c => string.IsNullOrWhiteSpace(c.TemplateName)))
                    c.TemplateName = _defaultTemplate;
            CommitHistory();
            RenderPreview();
            Status = $"Saved set defaults and filled blanks on {changed} existing card(s).";
        }
        else
        {
            CommitHistory();
            Status = _setProfile.IsEmpty && string.IsNullOrWhiteSpace(_defaultTemplate)
                ? "Cleared set defaults."
                : "Saved set defaults — new and imported cards will inherit them.";
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
    {
        foreach (var c in targets)
        {
            if (setCode != null) c.SetCode = setCode;
            if (artist != null) c.Artist = artist;
            if (rarity != null) c.Rarity = rarity;
            if (copyright != null) c.Copyright = copyright;
            if (templateName != null) c.TemplateName = templateName;
            if (setSymbolPath != null) c.SetSymbolPath = setSymbolPath;
        }
        MarkDirty();
        CommitHistory();
        // Re-sync the frame dropdown to the selected card, whose frame may have changed.
        if (_selectedCard != null)
        {
            var t = Templates.FirstOrDefault(x => x.Name == _selectedCard.TemplateName);
            if (t != null && !ReferenceEquals(t, _selectedTemplate)) SelectedTemplate = t;
        }
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
        var card = ActiveFace;
        if (card == null) { PreviewImage = null; UpdateValidation(null); return; }
        var template = TemplateFor(card);
        if (template == null) return;
        BitmapSource? inspectBmp = null;
        try
        {
            PreviewImage = _renderer.RenderToBitmap(card, template, supersample: 1, previewHints: true);
            if (_viewingFlipped && card.IsFlip)
            {
                // Same card, turned upside down — the flipped half reads upright.
                var turned = new TransformedBitmap(PreviewImage, new System.Windows.Media.RotateTransform(180));
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
        var card = face ?? ActiveFace;
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
            try { issues = LiveChecks.Merge(issues, RenderInspector.Inspect(inspectBmp, card, template.Spec)); }
            catch { /* keep the rule-based issues */ }
        }
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warns = issues.Count(i => i.Severity == IssueSeverity.Warning);

        if (errors == 0 && warns == 0)
        {
            ValidationSummary = "✓ No issues";
            ValidationDetails = "";
            HasValidationIssues = false;
            return;
        }

        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors} error{(errors > 1 ? "s" : "")}");
        if (warns > 0) parts.Add($"{warns} warning{(warns > 1 ? "s" : "")}");
        ValidationSummary = "⚠ " + string.Join(" · ", parts);
        ValidationDetails = string.Join("\n", issues
            .Where(i => i.Severity != IssueSeverity.Info)
            .OrderByDescending(i => i.Severity)
            .Select(i => "• " + i.Message));
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
        _projectFolder = null;   // ad-hoc until the first Save sets up a set folder
        _defaultTemplate = "";
        _setProfile = new SetProfile();
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
            Filter = "Cardinator project (*.cardinator;*.json)|*.cardinator;*.json|All files|*.*",
            InitialDirectory = AppPaths.SamplesDir,
        };
        if (dlg.ShowDialog() != true) return;
        LoadProjectFile(dlg.FileName);
    }

    /// <summary>Loads a project file into the window. Returns false (leaving the current project untouched)
    /// when the file couldn't be read, so callers can avoid mutating state on a failed load.</summary>
    private bool LoadProjectFile(string path)
    {
        try
        {
            var project = CardProject.Load(path);
            // Image paths are stored relative to the SET folder. A file opened out of a backups\ folder still
            // belongs to the set one level up, so resolve against that — otherwise restoring a backup would
            // point every card at a non-existent backups\art\… file.
            var projFolder = ProjectBackup.ArtRootFor(path);
            if (string.IsNullOrEmpty(projFolder)) projFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            _loading = true;
            Cards.Clear();
            foreach (var c in project.Cards)
            {
                // Art + set symbol are stored relative to the set folder; resolve them back to absolute paths in
                // memory (older projects saved absolute paths, which ResolveArt leaves untouched).
                if (projFolder != null)
                    CardProject.ResolveArt(c, projFolder);
                Cards.Add(c);
            }
            _projectName = string.IsNullOrWhiteSpace(project.Name)
                ? Path.GetFileNameWithoutExtension(path) : project.Name;
            _projectPath = path;
            _projectFolder = projFolder;
            _artBaseDir = project.ArtBaseDir;
            // Restore the set's default frame so new/imported cards inherit the house style (M4).
            _defaultTemplate = !string.IsNullOrWhiteSpace(project.DefaultTemplate)
                               && Templates.Any(t => t.Name == project.DefaultTemplate)
                ? project.DefaultTemplate : "";
            // Restore the set's shared metadata defaults (W1); resolve its symbol path back to absolute.
            _setProfile = project.Profile ?? new SetProfile();
            if (projFolder != null)
                _setProfile.SetSymbolPath = CardProject.ResolveArtPath(_setProfile.SetSymbolPath, projFolder);
            SelectedCard = Cards.FirstOrDefault();
            _loading = false;
            Dirty = false;
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            int missingArt = SetFolder.CountMissingArt(Cards);
            Status = $"Opened project with {Cards.Count} card(s)."
                     + (missingArt > 0 ? $"  ⚠ {missingArt} card(s) have missing art (was the art/ folder included?)." : "");
            SeedHistory();

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
        Dirty = true;
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
                InitialDirectory = AppPaths.SamplesDir,
            };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;

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
        try
        {
            // The file name IS the project's identity (there's no separate name field), so persist the
            // name derived from the path — otherwise a reloaded file would show a stale/"Untitled" name.
            var name = Path.GetFileNameWithoutExtension(path);
            var projFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;

            // Localize art → relative paths → back up the previous good file → atomic write. That exact
            // order is the data-safety guarantee, so it lives in ProjectWriter where it can be tested.
            int stranded = ProjectWriter.Write(path, Cards.ToList(), name, _artBaseDir, DefaultTemplateName, _setProfile);
            _projectPath = path;
            _projectFolder = projFolder;
            _projectName = name;
            MarkSavedPoint();   // this history position now matches disk (undo past it re-marks dirty)
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            Status = stranded == 0
                ? $"Saved project ({Cards.Count} cards) to {Path.GetFileName(path)}. Art in \\art, exports go to \\out."
                : $"Saved project ({Cards.Count} cards), but {stranded} image(s) couldn't be copied into the set — they won't travel if you move the folder.";
            return true;
        }
        catch (Exception ex)
        {
            Status = "Couldn't save project: " + ex.Message;
            return false;
        }
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
            ConfirmResult.Negative => true,
            _ => false,
        };
    }

    // --- import & batch -----------------------------------------------------

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import card list (names, or CSV/TSV)",
            Filter = "Card lists (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        await ImportListFile(dlg.FileName);
    }

    private async Task ImportListFile(string filePath)
    {
        if (Busy) return;
        ImportResult parsed;
        string baseDir;
        try
        {
            var content = File.ReadAllText(filePath);
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

    /// <summary>Imports a deck: paste an exported list or fetch a Moxfield deck by link — both land as
    /// text in the dialog and flow through the same parser (see <see cref="DeckListWindow"/>).</summary>
    private async void OnImportDeckList(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dlg = new DeckListWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var parsed = ImportService.ParseWithReport(dlg.DeckText, "", DefaultTemplateName);
        if (parsed.Cards.Count == 0)
        {
            Status = parsed.Skipped > 0
                ? $"No cards found in the pasted list ({parsed.Skipped} line(s) couldn't be read)."
                : "No cards found in the pasted list.";
            return;
        }
        await AddImportedAsync(parsed.Cards, "the deck list", parsed.Skipped);
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
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            MarkDirty();
            CommitHistory();   // fields filled on non-selected cards don't trip OnCardChanged, so commit explicitly
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            int dfc = imported.Count(i => i.Card.IsDoubleFaced);   // DFCs now import as one card with a back face
            Status = $"Imported {imported.Count} card(s). Scryfall filled {report.Filled}"
                     + (dfc > 0 ? $" ({dfc} double-faced)" : "")
                     + (report.NotFound.Count > 0 ? $"; {report.NotFound.Count} not found" : "")
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
                if (string.IsNullOrEmpty(c.TemplateName)) c.TemplateName = def;
                _setProfile.ApplyDefaults(c);   // W1: inherit the set's shared metadata, like every other add path
                Cards.Add(c);
            }
            SelectedCard = chosen[0];
            OnPropertyChanged(nameof(ProjectSummary));
            _ = _symbols.PrimeAsync(chosen.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));

            Status = $"Added {chosen.Count} card(s). Downloading art…";
            int art = 0;
            foreach (var c in chosen)
            {
                if (!string.IsNullOrWhiteSpace(c.ArtPath) || string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
                try { c.ArtPath = await ImageIntake.DownloadAsync(c.ArtUrl); art++; } catch { /* skip */ }
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
            var report = await BatchService.FillFromScryfallAsync(items, progress, percent: percent);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            MarkDirty();
            CommitHistory();   // fields filled in place don't trip OnCardChanged for non-selected cards
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            int dfc = items.Count(i => i.Card.IsDoubleFaced);   // DFCs fill as one card with a back face
            Status = $"Filled {report.Filled} card(s)"
                     + (dfc > 0 ? $" ({dfc} double-faced)" : "")
                     + (report.NotFound.Count > 0 ? $"; {report.NotFound.Count} not found." : ".");
        }
        catch (Exception ex)
        {
            Status = "Fill from Scryfall failed: " + ex.Message;
        }
        finally { ProgressIndeterminate = true; Busy = false; }
    }

    private async void OnExportAll(object sender, RoutedEventArgs e)
    {
        if (IsExporting) { Status = "An export is already running…"; return; }
        if (Cards.Count == 0) { Status = "No cards to export."; return; }
        var dlg = new OpenFolderDialog { Title = "Choose a folder to export all cards into", InitialDirectory = DefaultOutputDir() };
        if (dlg.ShowDialog() != true) return;

        // Deep-clone so edits made during the export can't tear reads on the render thread.
        var cardsSnapshot = Cards.Select(c => c.Clone()).ToList();
        var templatesSnapshot = Templates.ToList();
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
            var result = await RunStaAsync(() =>
                BatchService.ExportAll(cardsSnapshot, templatesSnapshot, folder, symbols, strProgress, pctProgress));

            _lastExportDir = folder;   // so "Open output folder" follows an Export-all
            Status = $"Exported {result.Exported}/{cardsSnapshot.Count} to {folder}."
                     + (result.Errors.Count > 0 ? $" {result.Errors.Count} error(s)." : "");
        }
        catch (Exception ex)
        {
            Status = "Export all failed: " + ex.Message;
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
            var names = Cards.Select(c => c.TemplateName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            int frames = SetPackager.ExportSetWithFrames(_projectPath, names, AppPaths.TemplatesDir, dlg.FileName);
            _lastExportDir = Path.GetDirectoryName(dlg.FileName);
            Status = $"Shared set to {Path.GetFileName(dlg.FileName)} ({Cards.Count} cards, {frames} custom frame(s) included).";
        }
        catch (Exception ex)
        {
            Status = "Couldn't share set: " + ex.Message;
        }
    }

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
                    ? SheetExporter.ComposeDoubleSided(cardsSnapshot, templatesSnapshot, symbols, PageSpec.Letter, BackRenderer.Render(supersample: 1), strProgress)
                    // Single-sided: expand DFC cards so both faces get a printable slot.
                    : SheetExporter.Compose(BatchService.ExpandFaces(cardsSnapshot), templatesSnapshot, symbols, PageSpec.Letter, strProgress);
                return SheetExporter.Save(pages, folder);
            });
            ExportProgress = 100;
            _lastExportDir = folder;   // so "Open output folder" opens where the sheet went
            Status = doubleSided
                ? $"Saved {paths.Count} page(s) — front+back, {folder}."
                : $"Saved {paths.Count} sheet page(s) (3x3) to {folder}.";
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

    private void OnEditDetails(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        new DetailsWindow(_selectedCard, _scryfall) { Owner = this }.ShowDialog();
        CommitHistory();   // coalesce the dialog's edits into one undo step and refresh the preview
        RenderPreview();
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
                if (TemplateImporter.IsBundlePath(path))
                {
                    name = TemplateImporter.ImportBundle(path);
                    Status = $"Imported template \"{name}\".";
                }
                else
                {
                    var fullArt = AskFrameFullArt();
                    if (fullArt == null) { Status = "Import cancelled."; return; }
                    name = TemplateImporter.CreateFromFile(Path.GetFileNameWithoutExtension(path), path, fullArt.Value);
                    Status = $"Imported frame as template \"{name}\". Tune its regions in CardinatorData/templates.";
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
                Status = $"Imported frame as template \"{name}\". Tune its regions in CardinatorData/templates.";
            }
            RefreshTemplates(name);
        }
        catch (Exception ex)
        {
            Status = "Couldn't import: " + ex.Message;
        }
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

    /// <summary>Exports the selected template as a shareable bundle (frame.png + template.json in one file).</summary>
    private void OnExportTemplate(object sender, RoutedEventArgs e)
    {
        var t = _selectedTemplate ?? Templates.FirstOrDefault();
        if (t == null) { Status = "No frame selected to export."; return; }
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
        var t = _selectedTemplate ?? Templates.FirstOrDefault();
        if (t == null) { Status = "No frame selected to delete."; return; }

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
        var t = _selectedTemplate ?? Templates.FirstOrDefault();
        if (t == null) { Status = "No frame selected to design."; return; }
        try
        {
            var dir = Path.GetDirectoryName(t.FramePath);
            var slug = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            var win = new FrameDesignWindow(t) { Owner = this, IsBuiltIn = TemplateService.IsBuiltIn(slug) };
            if (win.ShowDialog() == true)
            {
                RefreshTemplates(win.AppliedTemplateName);
                RenderPreview();
                Status = $"Updated frame design for \"{win.AppliedTemplateName}\".";
            }
        }
        catch (Exception ex)
        {
            Status = "Frame design failed: " + ex.Message;
        }
    }

    /// <summary>Reloads the template list from disk (e.g. after importing a new frame).</summary>
    private void RefreshTemplates(string? selectName = null)
    {
        var wanted = selectName ?? _selectedTemplate?.Name;
        Templates.Clear();
        foreach (var t in _templates.LoadAll()) Templates.Add(t);
        SelectedTemplate = Templates.FirstOrDefault(t => t.Name == wanted) ?? Templates.FirstOrDefault();
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
            if (report.Total > 0) { MarkDirty(); CommitHistory(); }
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
            // Non-destructive: fill from the looked-up face but never blank a field the user already typed.
            CardDetailsFill.MergeNonEmpty(card, f);

            // A genuinely two-sided card becomes ONE card with a back face (flip + indicator + a -back.png on
            // export), exactly as batch import does. A split/flip/aftermath card is printed on one side, so
            // its other half is added as its own card.
            foreach (var extra in CardDetailsFill.AttachFaces(card, faces))
            {
                extra.TemplateName = card.TemplateName;
                _setProfile.ApplyDefaults(extra);
                Cards.Add(extra);
            }

            _ = _symbols.PrimeAsync(faces.SelectMany(x => ManaText.SymbolTokens(x.ManaCost, x.RulesText)));

            // Pull the real art from Scryfall only for a face that doesn't already have art — a card's own
            // art (and its frame) are never replaced by a lookup. Remember whether this card already had art
            // so the status line can honestly say what changed.
            bool hadArt = !string.IsNullOrWhiteSpace(card.ArtPath);
            bool gotArt = await FillArtFromScryfall(card, f.ArtUrl);
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
            Status = card.IsDoubleFaced
                ? $"Loaded “{f.Name}” from Scryfall as one double-faced card — use Show back to flip.{artNote}"
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
        var local = ImageIntake.EnsureLocalCopy(path);
        card.ArtPath = local;
        card.ArtScale = 1.0;
        card.ArtOffsetX = 0;
        card.ArtOffsetY = 0;
        Status = "Loaded art: " + Path.GetFileName(local) + (card.IsBackFace ? " (back face)" : "");
    }

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
        var template = _selectedTemplate ?? Templates.FirstOrDefault();
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
            var bmp = _renderer.RenderToBitmap(_selectedCard, template, supersample: 2);
            CardExporter.Save(bmp, dlg.FileName);   // PNG or JPEG by extension
            _lastExportDir = Path.GetDirectoryName(dlg.FileName);   // so "Open output folder" opens here

            // Double-faced: write the back alongside as "<name>-back<ext>".
            if (_selectedCard.BackFace is { } back)
            {
                var backPath = BackFacePath(dlg.FileName);
                CardExporter.Save(_renderer.RenderToBitmap(back, TemplateFor(back) ?? template, supersample: 2), backPath);
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
    private readonly System.Diagnostics.Stopwatch _liveClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastLiveRenderMs = long.MinValue;

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PreviewImageControl.Focus();   // so the arrow keys nudge the art after you click the preview
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

        card.ArtOffsetX = Clamp(card.ArtOffsetX + PanSign * (p.X - _panStart.X) / w, -0.5, 0.5);
        card.ArtOffsetY = Clamp(card.ArtOffsetY + PanSign * (p.Y - _panStart.Y) / h, -0.5, 0.5);
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
        card.ArtOffsetX = Clamp(card.ArtOffsetX + PanSign * dx, -0.5, 0.5);
        card.ArtOffsetY = Clamp(card.ArtOffsetY + PanSign * dy, -0.5, 0.5);
        RenderPreview();
    }

    /// <summary>Zooms the art by a small step (used by the +/- keys).</summary>
    private void ZoomArt(double delta)
    {
        var card = ActiveFace;
        if (card == null) return;
        card.ArtScale = Clamp(card.ArtScale + delta, 0.5, 3.0);
        RenderPreview();
    }

    /// <summary>A throttled immediate re-render (~60fps) for smooth drag/zoom without flooding the UI thread.</summary>
    private void RenderPreviewLive()
    {
        var now = _liveClock.ElapsedMilliseconds;
        if (now - _lastLiveRenderMs < 16) return;
        _lastLiveRenderMs = now;
        RenderPreview(inspect: false);   // skip the extra pixel-inspection render while dragging, for smoothness
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

            var image = files.FirstOrDefault(ImageIntake.LooksLikeImagePath);
            if (image == null) { Status = "That file type isn't an image, project, or card list."; return; }
            if (ActiveFace == null) { Status = "Select a card first, then drop art onto it."; return; }
            SetArt(image);
        }
        catch (Exception ex)
        {
            Status = "Couldn't handle that drop: " + ex.Message;
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        var card = ActiveFace;
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
            }
        }

        // Ctrl+Shift+Z is a common "redo" alias.
        if (mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)
            && e.Key == System.Windows.Input.Key.Z)
        {
            Redo(); e.Handled = true; return;
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

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (SuppressClosePrompt) return;
        if (!ConfirmDiscardIfDirty()) e.Cancel = true;
    }

    // --- INotifyPropertyChanged ---------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
