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

        // Download authentic Scryfall symbols in the background; re-render as they arrive.
        _symbols.Updated += OnSymbolsUpdated;
        _ = _symbols.PrimeAsync();
    }

    // --- bindable state -----------------------------------------------------

    public ObservableCollection<Template> Templates { get; }
    public ObservableCollection<CardModel> Cards { get; }

    public CardModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (_selectedCard != null) _selectedCard.PropertyChanged -= OnCardChanged;
            _selectedCard = value;
            if (_selectedCard != null)
            {
                _selectedCard.PropertyChanged += OnCardChanged;
                var t = Templates.FirstOrDefault(x => x.Name == _selectedCard.TemplateName);
                if (t != null) { _selectedTemplate = t; OnPropertyChanged(nameof(SelectedTemplate)); }
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanEditSelected));
            RenderPreview();
        }
    }

    public bool HasSelection => _selectedCard != null;

    /// <summary>Short app version shown in the header (e.g. "v1.3"), so the running build is obvious.</summary>
    public string AppVersion => "v" + (App.Version.Split(' ').LastOrDefault() ?? "");

    /// <summary>True when the project has at least one card (gates batch/export actions).</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>A long/mutating operation (lookup, import, search, export…) is running. While true, other
    /// actions are disabled and re-entrant handlers early-return, so nothing conflicts or double-fires.</summary>
    public bool Busy
    {
        get => _busy;
        private set
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

    public string WindowTitle => (_dirty ? "● " : "") + _projectName + " — Cardinator";

    /// <summary>Tracks unsaved edits so we can warn before losing work and mark the title bar.</summary>
    public bool Dirty
    {
        get => _dirty;
        private set { _dirty = value; OnPropertyChanged(nameof(WindowTitle)); }
    }

    private void MarkDirty() { if (!_loading) Dirty = true; }

    private string DefaultTemplateName => _selectedTemplate?.Name ?? Templates.FirstOrDefault()?.Name ?? "";

    // --- rendering ----------------------------------------------------------

    /// <summary>Background symbol-prime finished — re-render, unless the window/dispatcher is shutting down
    /// (a late callback from an in-flight prime must not throw on a dead dispatcher).</summary>
    private void OnSymbolsUpdated()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(RenderPreview)); } catch { /* shutting down */ }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        _renderTimer.Stop();
        _renderTimer.Start();   // debounce rapid edits (typing, slider drags)
        QueueUndoCommit();
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
        if (_histIdx >= 0 && _history[_histIdx].json == json && _history[_histIdx].sel == sel) return;
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
            if (_selectedCard != null) _selectedCard.PropertyChanged -= OnCardChanged;
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
        CommitHistory();               // flush any pending edit so it can be redone
        if (_histIdx <= 0) return;
        _histIdx--;
        RestoreSnapshot(_history[_histIdx]);
        UpdateUndoRedo();
        Status = "Undo.";
    }

    public void Redo()
    {
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

    /// <summary>Assigns collector numbers in list order: 001/N, 002/N, … (zero-padded to N's width).</summary>
    private void OnNumberCards(object sender, RoutedEventArgs e)
    {
        int total = Cards.Count;
        if (total == 0) { Status = "No cards to number."; return; }
        int width = total.ToString().Length;
        for (int i = 0; i < total; i++)
            Cards[i].CollectorNumber = $"{(i + 1).ToString().PadLeft(width, '0')}/{total}";
        MarkDirty();
        CommitHistory();
        RenderPreview();
        Status = $"Numbered {total} card(s) (001/{total} style).";
    }

    /// <summary>Sets the same set code / artist / rarity / copyright / frame on every card at once.</summary>
    private void OnBulkEdit(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) { Status = "No cards to edit."; return; }
        var dlg = new BulkEditWindow(Templates.Select(t => t.Name)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        ApplyBulkEdit(dlg.SetCode, dlg.Artist, dlg.Rarity, dlg.Copyright, dlg.TemplateName, dlg.SetSymbolPath);
        Status = $"Applied changes to {Cards.Count} card(s).";
    }

    /// <summary>Applies bulk field changes to every card and refreshes the live preview. A null value
    /// leaves that field unchanged; an empty string clears it. Exposed for testing the live re-render.</summary>
    internal void ApplyBulkEdit(string? setCode, string? artist, string? rarity,
        string? copyright, string? templateName, string? setSymbolPath)
    {
        foreach (var c in Cards)
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

    private void RenderPreview()
    {
        if (_selectedCard == null) { PreviewImage = null; UpdateValidation(); return; }
        var template = _selectedTemplate ?? Templates.FirstOrDefault();
        if (template == null) return;
        try
        {
            PreviewImage = _renderer.RenderToBitmap(_selectedCard, template, supersample: 1, previewHints: true);
        }
        catch (Exception ex)
        {
            Status = "Preview error: " + ex.Message;
        }
        UpdateValidation();
    }

    /// <summary>Runs the automated checks on the selected card and updates the CHECKS panel. Cheap
    /// (no rendering), so it runs on every edit alongside the live preview — the friend sees problems
    /// (missing art, bad symbols, overlaps, duplicate collector numbers) as they happen, not on export.</summary>
    private void UpdateValidation()
    {
        var card = _selectedCard;
        var template = _selectedTemplate ?? Templates.FirstOrDefault();
        if (card == null || template == null)
        {
            ValidationSummary = ""; ValidationDetails = ""; HasValidationIssues = false;
            return;
        }

        var issues = CardValidator.Validate(card, template.Spec, Cards);
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
        if (!ConfirmDiscardIfDirty()) return;
        _loading = true;
        Cards.Clear();
        _projectName = "Untitled Project";
        _projectPath = "";
        _projectFolder = null;   // ad-hoc until the first Save sets up a set folder
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

    private void LoadProjectFile(string path)
    {
        try
        {
            var project = CardProject.Load(path);
            var projFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            _loading = true;
            Cards.Clear();
            foreach (var c in project.Cards)
            {
                // Art is stored relative to the set folder; resolve it back to an absolute path in memory
                // (older projects saved absolute paths, which ResolveArtPath leaves untouched).
                if (projFolder != null)
                    c.ArtPath = CardProject.ResolveArtPath(c.ArtPath, projFolder);
                Cards.Add(c);
            }
            _projectName = string.IsNullOrWhiteSpace(project.Name)
                ? Path.GetFileNameWithoutExtension(path) : project.Name;
            _projectPath = path;
            _projectFolder = projFolder;
            _artBaseDir = project.ArtBaseDir;
            SelectedCard = Cards.FirstOrDefault();
            _loading = false;
            Dirty = false;
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            Status = $"Opened project with {Cards.Count} card(s).";
            SeedHistory();
        }
        catch (Exception ex)
        {
            _loading = false;
            Status = "Couldn't open project: " + ex.Message;
        }
    }

    private void OnSaveProject(object sender, RoutedEventArgs e) => SaveProject(forceDialog: false);

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
        }
        try
        {
            // The file name IS the project's identity (there's no separate name field), so persist the
            // name derived from the path — otherwise a reloaded file would show a stale/"Untitled" name.
            var name = Path.GetFileNameWithoutExtension(path);
            var projFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;

            // Make the set self-contained: copy any external art into <set>\art and repoint the cards at it,
            // so the folder can be moved/zipped/shared and still render. Exports default to <set>\out.
            LocalizeArtInto(projFolder);
            try { Directory.CreateDirectory(Path.Combine(projFolder, "out")); } catch { /* best effort */ }

            // Serialize with art paths made relative to the set folder (resolved back to absolute on load).
            var project = new CardProject
            {
                Name = name,
                ArtBaseDir = _artBaseDir,
                DefaultTemplate = DefaultTemplateName,
                Cards = Cards.Select(c => CloneWithRelativeArt(c, projFolder)).ToList(),
            };
            project.Save(path);
            _projectPath = path;
            _projectFolder = projFolder;
            _projectName = name;
            MarkSavedPoint();   // this history position now matches disk (undo past it re-marks dirty)
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            Status = $"Saved project ({Cards.Count} cards) to {Path.GetFileName(path)}. Art in \\art, exports go to \\out.";
            return true;
        }
        catch (Exception ex)
        {
            Status = "Couldn't save project: " + ex.Message;
            return false;
        }
    }

    /// <summary>Copies each card's art that lives outside this set's <c>art</c> folder into it, and repoints
    /// the card at the copy — so the saved set folder is self-contained (movable/zippable). Art already inside
    /// the folder is left alone, so re-saving doesn't duplicate.</summary>
    private void LocalizeArtInto(string projFolder)
    {
        var artDir = Path.Combine(projFolder, "art");
        var artRoot = Path.GetFullPath(artDir) + Path.DirectorySeparatorChar;
        try { Directory.CreateDirectory(artDir); } catch { return; }

        foreach (var c in Cards)
        {
            if (string.IsNullOrWhiteSpace(c.ArtPath)) continue;
            string full;
            try { full = Path.GetFullPath(c.ArtPath); } catch { continue; }
            if (!File.Exists(full)) continue;
            if (full.StartsWith(artRoot, StringComparison.OrdinalIgnoreCase)) continue;   // already in this set

            var dest = Path.Combine(artDir,
                ImageIntake.UniqueFileName(Path.GetFileNameWithoutExtension(full), Path.GetExtension(full)));
            try { File.Copy(full, dest, overwrite: false); c.ArtPath = dest; }
            catch { /* keep the original path if the copy fails */ }
        }
    }

    /// <summary>A clone of the card whose art path is relative to the set folder (so the saved file is
    /// portable). Paths outside the folder stay absolute.</summary>
    private static CardModel CloneWithRelativeArt(CardModel card, string projFolder)
    {
        var clone = card.Clone();
        clone.ArtPath = CardProject.RelativeArtPath(clone.ArtPath, projFolder);   // e.g. "art\foo.png"
        return clone;
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
        List<ImportedCard> imported;
        string baseDir;
        try
        {
            var content = File.ReadAllText(filePath);
            baseDir = Path.GetDirectoryName(filePath) ?? "";
            imported = ImportService.Parse(content, baseDir, DefaultTemplateName);
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; return; }

        if (imported.Count == 0) { Status = "No cards found in that file."; return; }
        _artBaseDir = baseDir;   // only commit the base dir once we know the import actually produced cards
        await AddImportedAsync(imported, "that file");
    }

    /// <summary>Imports a deck: paste an exported list or fetch a Moxfield deck by link — both land as
    /// text in the dialog and flow through the same parser (see <see cref="DeckListWindow"/>).</summary>
    private async void OnImportDeckList(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dlg = new DeckListWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var imported = ImportService.Parse(dlg.DeckText, "", DefaultTemplateName);
        if (imported.Count == 0) { Status = "No cards found in the pasted list."; return; }
        await AddImportedAsync(imported, "the deck list");
    }

    /// <summary>Adds parsed cards to the project and fills their blank fields + art from Scryfall,
    /// with a live progress bar. Shared by file import and deck-list import.</summary>
    private async Task AddImportedAsync(List<ImportedCard> imported, string source)
    {
        if (Busy) return;
        Busy = true;
        ProgressIndeterminate = false;   // we know the card count, so show real percentage
        ExportProgress = 0;
        try
        {
            foreach (var item in imported) Cards.Add(item.Card);
            SelectedCard = imported[0].Card;
            OnPropertyChanged(nameof(ProjectSummary));

            int needLookup = imported.Count(i => i.NeedsLookup);
            Status = $"Imported {imported.Count} card(s) from {source}. Filling {needLookup} from Scryfall…";

            var progress = new Progress<string>(s => Status = s);
            var percent = new Progress<double>(p => ExportProgress = p * 100);
            var report = await BatchService.FillFromScryfallAsync(imported, progress, percent: percent);
            foreach (var back in report.ExtraBackFaces) Cards.Add(back);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            MarkDirty();
            CommitHistory();   // fields filled on non-selected cards don't trip OnCardChanged, so commit explicitly
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            Status = $"Imported {imported.Count} card(s). Scryfall filled {report.Filled}"
                     + (report.ExtraBackFaces.Count > 0 ? $" (+{report.ExtraBackFaces.Count} back face)" : "")
                     + (report.NotFound.Count > 0 ? $"; {report.NotFound.Count} not found." : ".");
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
            foreach (var back in report.ExtraBackFaces) Cards.Add(back);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            MarkDirty();
            CommitHistory();   // fields filled in place don't trip OnCardChanged for non-selected cards
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            Status = $"Filled {report.Filled} card(s)"
                     + (report.ExtraBackFaces.Count > 0 ? $" (+{report.ExtraBackFaces.Count} back face)" : "")
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
        var dlg = new OpenFolderDialog { Title = "Choose a folder to export all cards into" };
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
                    : SheetExporter.Compose(cardsSnapshot, templatesSnapshot, symbols, PageSpec.Letter, strProgress);
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
                    name = TemplateImporter.CreateFromFile(Path.GetFileNameWithoutExtension(path), path);
                    Status = $"Imported frame as template \"{name}\". Tune its regions in CardinatorData/templates.";
                }
            }
            else
            {
                var url = prompt.Value;
                if (url.Length == 0) { Status = "Nothing to import — choose a file or paste a link."; return; }
                if (!ImageIntake.IsHttpUrl(url)) { Status = "That doesn't look like a web link."; return; }
                Status = "Downloading frame…";
                name = await TemplateImporter.CreateFromUrlAsync("", url);
                Status = $"Imported frame as template \"{name}\". Tune its regions in CardinatorData/templates.";
            }
            RefreshTemplates(name);
        }
        catch (Exception ex)
        {
            Status = "Couldn't import: " + ex.Message;
        }
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

        if (ConfirmDialog.Show(this, "Delete frame",
                $"Delete the frame “{t.Name}”? This removes it from your templates folder and can't be undone.",
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
            int matched = ArtMatcher.MatchInto(Cards, dlg.FolderName, overwrite: false);
            _artBaseDir = dlg.FolderName;
            if (matched > 0) { MarkDirty(); CommitHistory(); }
            RenderPreview();
            Status = matched > 0 ? $"Matched art for {matched} card(s)." : "No filename matches found.";
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
            card.Name = f.Name;
            card.ManaCost = f.ManaCost;
            card.TypeLine = f.TypeLine;
            card.RulesText = f.RulesText;
            card.Power = f.Power;
            card.Toughness = f.Toughness;
            card.Loyalty = f.Loyalty;
            card.SetCode = f.SetCode;
            card.CollectorNumber = f.CollectorNumber;
            card.Rarity = f.Rarity;

            // Double-faced: add the back as a new card in the project.
            for (int i = 1; i < faces.Count; i++)
            {
                faces[i].TemplateName = card.TemplateName;
                Cards.Add(faces[i]);
            }

            _ = _symbols.PrimeAsync(faces.SelectMany(x => ManaText.SymbolTokens(x.ManaCost, x.RulesText)));

            // Pull the real art from Scryfall only for a face that doesn't already have art — a card's own
            // art (and its frame) are never replaced by a lookup. Remember whether this card already had art
            // so the status line can honestly say what changed.
            bool hadArt = !string.IsNullOrWhiteSpace(card.ArtPath);
            bool gotArt = await FillArtFromScryfall(card, f.ArtUrl);
            for (int i = 1; i < faces.Count; i++)
                await FillArtFromScryfall(faces[i], faces[i].ArtUrl);

            MarkDirty();
            CommitHistory();
            RenderPreview();
            // Lookup fills the text fields; the frame is always kept, and existing art is kept too.
            string artNote = hadArt
                ? " Your art and frame are unchanged."
                : gotArt ? " Added matching art; your frame is unchanged."
                         : " No art found; your frame is unchanged.";
            Status = faces.Count > 1
                ? $"Loaded “{f.Name}” details (+{faces.Count - 1} back face) from Scryfall.{artNote}"
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
    private void SetArt(string path)
    {
        if (_selectedCard == null) return;
        var local = ImageIntake.EnsureLocalCopy(path);
        _selectedCard.ArtPath = local;
        _selectedCard.ArtScale = 1.0;
        _selectedCard.ArtOffsetX = 0;
        _selectedCard.ArtOffsetY = 0;
        Status = "Loaded art: " + Path.GetFileName(local);
    }

    private void OnClearArt(object sender, RoutedEventArgs e)
    {
        if (_selectedCard != null) _selectedCard.ArtPath = "";
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
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
            Status = $"Exported {Path.GetFileName(dlg.FileName)} ({bmp.PixelWidth}x{bmp.PixelHeight}).";
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    // --- drag-to-pan / wheel-zoom art on the preview ------------------------

    private bool _isPanning;
    private System.Windows.Point _panStart;
    private readonly System.Diagnostics.Stopwatch _liveClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastLiveRenderMs = long.MinValue;

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PreviewImageControl.Focus();   // so the arrow keys nudge the art after you click the preview
        if (_selectedCard == null || string.IsNullOrWhiteSpace(_selectedCard.ArtPath)) return;
        _isPanning = true;
        _panStart = e.GetPosition(PreviewImageControl);
        PreviewImageControl.CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning || _selectedCard == null) return;
        var p = e.GetPosition(PreviewImageControl);
        var (w, h) = DisplayedCardSize();
        if (w <= 0 || h <= 0) return;

        _selectedCard.ArtOffsetX = Clamp(_selectedCard.ArtOffsetX + (p.X - _panStart.X) / w, -0.5, 0.5);
        _selectedCard.ArtOffsetY = Clamp(_selectedCard.ArtOffsetY + (p.Y - _panStart.Y) / h, -0.5, 0.5);
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
        if (_selectedCard == null || string.IsNullOrWhiteSpace(_selectedCard.ArtPath)) return;
        // Finer by default; Ctrl = ultra-fine, Shift = coarse. (Was a fixed 0.08/notch — too jumpy.)
        var mods = System.Windows.Input.Keyboard.Modifiers;
        double per = mods.HasFlag(System.Windows.Input.ModifierKeys.Control) ? 0.01
                   : mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 0.08
                   : 0.03;
        _selectedCard.ArtScale = Clamp(_selectedCard.ArtScale + e.Delta / 120.0 * per, 0.5, 3.0);
        RenderPreviewLive();
    }

    /// <summary>Nudges the art by a fraction of the card (dx/dy in 0..1 card-space). Used by the arrow keys.</summary>
    private void NudgeArt(double dx, double dy)
    {
        if (_selectedCard == null) return;
        _selectedCard.ArtOffsetX = Clamp(_selectedCard.ArtOffsetX + dx, -0.5, 0.5);
        _selectedCard.ArtOffsetY = Clamp(_selectedCard.ArtOffsetY + dy, -0.5, 0.5);
        RenderPreview();
    }

    /// <summary>Zooms the art by a small step (used by the +/- keys).</summary>
    private void ZoomArt(double delta)
    {
        if (_selectedCard == null) return;
        _selectedCard.ArtScale = Clamp(_selectedCard.ArtScale + delta, 0.5, 3.0);
        RenderPreview();
    }

    /// <summary>A throttled immediate re-render (~60fps) for smooth drag/zoom without flooding the UI thread.</summary>
    private void RenderPreviewLive()
    {
        var now = _liveClock.ElapsedMilliseconds;
        if (now - _lastLiveRenderMs < 16) return;
        _lastLiveRenderMs = now;
        RenderPreview();
    }

    /// <summary>Size of the letterboxed card inside the preview Image (5:7 aspect).</summary>
    private (double w, double h) DisplayedCardSize()
    {
        double ew = PreviewImageControl.ActualWidth, eh = PreviewImageControl.ActualHeight;
        if (ew <= 0 || eh <= 0) return (0, 0);
        double h = Math.Min(eh, ew * 1050.0 / 750.0);
        return (h * 750.0 / 1050.0, h);
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
        if (_selectedCard == null) return;
        try
        {
            // A real bitmap on the clipboard (e.g. a browser's "Copy image"). Read it robustly: the plain
            // Clipboard.GetImage() yields an all-black image for browser DIBs whose alpha channel is zeroed.
            var bmp = TryGetClipboardImage();
            if (bmp != null) { SetArt(ImageIntake.SaveBitmap(bmp)); return; }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var file = System.Windows.Clipboard.GetFileDropList().Cast<string>()
                    .FirstOrDefault(ImageIntake.LooksLikeImagePath);
                if (file != null) { SetArt(file); return; }
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (ImageIntake.IsHttpUrl(text)) { await DownloadArtAsync(text); return; }
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

    private async Task DownloadArtAsync(string url)
    {
        Status = "Downloading art…";
        try
        {
            var path = await ImageIntake.DownloadAsync(url);
            SetArt(path);
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
            if (_selectedCard == null) { Status = "Select a card first, then drop art onto it."; return; }
            SetArt(image);
        }
        catch (Exception ex)
        {
            Status = "Couldn't handle that drop: " + ex.Message;
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        var template = _selectedTemplate ?? Templates.FirstOrDefault();
        if (template == null) return;
        try
        {
            var bmp = _renderer.RenderToBitmap(_selectedCard, template, supersample: 2);
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
            && _selectedCard != null && !string.IsNullOrWhiteSpace(_selectedCard.ArtPath))
        {
            bool shift = mods.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            double sx = (shift ? 10.0 : 1.0) / 750.0, sy = (shift ? 10.0 : 1.0) / 1050.0;   // 1px (or 10px) of the card
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
