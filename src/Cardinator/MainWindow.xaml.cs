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
    private bool _isExporting;
    private double _exportProgress;
    private bool _dirty;
    private bool _loading;                 // suppress dirty-tracking while populating a project

    // Undo/redo: full-project snapshots (serialized cards + selected index). A debounced commit coalesces
    // rapid edits into one step; discrete changes (add/delete/import/lookup/art) are captured too because
    // they trip either the card's PropertyChanged or the Cards collection change.
    private readonly List<(string json, int sel)> _history = new();
    private int _histIdx = -1;
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
        Cards.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ProjectSummary)); MarkDirty(); QueueUndoCommit(); };

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
            RenderPreview();
        }
    }

    public bool HasSelection => _selectedCard != null;

    public Template? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            _selectedTemplate = value;
            OnPropertyChanged();
            if (value != null && _selectedCard != null) _selectedCard.TemplateName = value.Name;
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
        set { _isExporting = value; OnPropertyChanged(); }
    }

    public double ExportProgress
    {
        get => _exportProgress;
        set { _exportProgress = value; OnPropertyChanged(); }
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
    public bool CanUndo => _histIdx > 0;
    /// <summary>Whether there's a forward state to redo.</summary>
    public bool CanRedo => _histIdx >= 0 && _histIdx < _history.Count - 1;

    private void SeedHistory()
    {
        _history.Clear();
        _history.Add((SnapshotJson(), SelectedIndex()));
        _histIdx = 0;
        UpdateUndoRedo();
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
        while (_history.Count > cap) { _history.RemoveAt(0); _histIdx--; }
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
        MarkDirty();
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

        foreach (var c in Cards)
        {
            if (dlg.SetCode != null) c.SetCode = dlg.SetCode;
            if (dlg.Artist != null) c.Artist = dlg.Artist;
            if (dlg.Rarity != null) c.Rarity = dlg.Rarity;
            if (dlg.Copyright != null) c.Copyright = dlg.Copyright;
            if (dlg.TemplateName != null) c.TemplateName = dlg.TemplateName;
            if (dlg.SetSymbolPath != null) c.SetSymbolPath = dlg.SetSymbolPath;
        }
        MarkDirty();
        CommitHistory();
        RenderPreview();
        Status = $"Applied changes to {Cards.Count} card(s).";
    }

    private void MoveSelected(int delta)
    {
        if (_selectedCard == null) return;
        int i = Cards.IndexOf(_selectedCard);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Cards.Count) return;
        Cards.Move(i, j);
        CardList.SelectedItem = _selectedCard;   // keep the moved card selected/highlighted
        MarkDirty();
        CommitHistory();
        Status = "Reordered cards.";
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
        if (_selectedCard == null) return;
        int idx = Cards.IndexOf(_selectedCard);
        Cards.Remove(_selectedCard);
        SelectedCard = Cards.Count == 0 ? null : Cards[Math.Min(idx, Cards.Count - 1)];
        Status = "Deleted card.";
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;
        _loading = true;
        Cards.Clear();
        _projectName = "Untitled Project";
        _projectPath = "";
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
            _loading = true;
            Cards.Clear();
            foreach (var c in project.Cards) Cards.Add(c);
            _projectName = string.IsNullOrWhiteSpace(project.Name)
                ? Path.GetFileNameWithoutExtension(path) : project.Name;
            _projectPath = path;
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
            var project = new CardProject
            {
                Name = _projectName,
                ArtBaseDir = _artBaseDir,
                DefaultTemplate = DefaultTemplateName,
                Cards = Cards.ToList(),
            };
            project.Save(path);
            _projectPath = path;
            _projectName = Path.GetFileNameWithoutExtension(path);
            Dirty = false;
            OnPropertyChanged(nameof(ProjectSummary));
            OnPropertyChanged(nameof(WindowTitle));
            Status = $"Saved project ({Cards.Count} cards).";
            return true;
        }
        catch (Exception ex)
        {
            Status = "Couldn't save project: " + ex.Message;
            return false;
        }
    }

    /// <summary>Returns true if it's OK to proceed (nothing unsaved, or the user chose to discard/saved).</summary>
    private bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        var choice = MessageBox.Show(this,
            "You have unsaved changes. Save them first?",
            "Cardinator", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return choice switch
        {
            MessageBoxResult.Yes => SaveProject(forceDialog: false),
            MessageBoxResult.No => true,
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
        try
        {
            var content = File.ReadAllText(filePath);
            _artBaseDir = Path.GetDirectoryName(filePath) ?? "";
            var imported = ImportService.Parse(content, _artBaseDir, DefaultTemplateName);
            if (imported.Count == 0) { Status = "No cards found in that file."; return; }

            foreach (var item in imported) Cards.Add(item.Card);
            SelectedCard = imported[0].Card;
            OnPropertyChanged(nameof(ProjectSummary));

            int needLookup = imported.Count(i => i.NeedsLookup);
            Status = $"Imported {imported.Count} card(s). Looking up {needLookup} on Scryfall…";

            var progress = new Progress<string>(s => Status = s);
            var report = await BatchService.FillFromScryfallAsync(imported, progress);
            foreach (var back in report.ExtraBackFaces) Cards.Add(back);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
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
    }

    private async void OnScryfallSearch(object sender, RoutedEventArgs e)
    {
        var query = InputDialog.Ask(this, "Scryfall search",
            "e.g.  t:dragon   ·   set:dom   ·   c:r cmc=1     (imports up to 60 cards, with art)");
        if (query == null) return;

        Status = $"Searching Scryfall for \"{query}\"…";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var found = await _scryfall.SearchAsync(query, 60, progress);
            if (found.Count == 0) { Status = $"No cards matched \"{query}\"."; return; }

            var def = DefaultTemplateName;
            foreach (var c in found)
            {
                if (string.IsNullOrEmpty(c.TemplateName)) c.TemplateName = def;
                Cards.Add(c);
            }
            SelectedCard = found[0];
            OnPropertyChanged(nameof(ProjectSummary));
            _ = _symbols.PrimeAsync(found.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));

            Status = $"Added {found.Count} card(s). Downloading art…";
            int art = 0;
            foreach (var c in found)
            {
                if (!string.IsNullOrWhiteSpace(c.ArtPath) || string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
                try { c.ArtPath = await ImageIntake.DownloadAsync(c.ArtUrl); art++; } catch { /* skip */ }
            }
            RenderPreview();
            Status = $"Imported {found.Count} card(s) from search ({art} with art).";
        }
        catch (Exception ex)
        {
            Status = "Search failed: " + ex.Message;
        }
    }

    private async void OnLookupMissing(object sender, RoutedEventArgs e)
    {
        var items = Cards.Select(c => new ImportedCard(
            c, string.IsNullOrWhiteSpace(c.ManaCost)
               && string.IsNullOrWhiteSpace(c.TypeLine)
               && string.IsNullOrWhiteSpace(c.RulesText))).ToList();
        int n = items.Count(i => i.NeedsLookup);
        if (n == 0) { Status = "No cards are missing text to look up."; return; }

        Status = $"Looking up {n} card(s) on Scryfall…";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var report = await BatchService.FillFromScryfallAsync(items, progress);
            foreach (var back in report.ExtraBackFaces) Cards.Add(back);
            _ = _symbols.PrimeAsync(Cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)));
            RenderPreview();
            OnPropertyChanged(nameof(ProjectSummary));
            Status = $"Filled {report.Filled} card(s)"
                     + (report.ExtraBackFaces.Count > 0 ? $" (+{report.ExtraBackFaces.Count} back face)" : "")
                     + (report.NotFound.Count > 0 ? $"; {report.NotFound.Count} not found." : ".");
        }
        catch (Exception ex)
        {
            Status = "Look up missing failed: " + ex.Message;
        }
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

        IsExporting = true;
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
        }
    }

    private async void OnExportSheet(object sender, RoutedEventArgs e)
    {
        if (IsExporting) { Status = "An export is already running…"; return; }
        if (Cards.Count == 0) { Status = "No cards to export."; return; }

        // Offer double-sided (adds a mirrored back page after each front page for duplex printing).
        var duplexAnswer = MessageBox.Show(this,
            "Include card backs for double-sided printing?\n\nYes  — front + back pages (flip on the long edge)\nNo   — fronts only",
            "Print sheet", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (duplexAnswer == MessageBoxResult.Cancel) return;
        bool doubleSided = duplexAnswer == MessageBoxResult.Yes;

        var dlg = new OpenFolderDialog { Title = "Choose a folder for the printable sheet pages" };
        if (dlg.ShowDialog() != true) return;

        // Deep-clone so edits during the (background) compose can't tear reads on the render thread.
        var cardsSnapshot = Cards.Select(c => c.Clone()).ToList();
        var templatesSnapshot = Templates.ToList();
        var symbols = _symbols;
        var folder = dlg.FolderName;

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
        }
    }

    private void OnEditDetails(object sender, RoutedEventArgs e)
    {
        if (_selectedCard == null) return;
        new DetailsWindow(_selectedCard) { Owner = this }.ShowDialog();
    }

    private void OnHelp(object sender, RoutedEventArgs e)
        => new HelpWindow(App.Version) { Owner = this }.ShowDialog();

    private async void OnImportFrame(object sender, RoutedEventArgs e)
    {
        // Offer a URL first; leaving it blank falls back to picking a local file.
        var url = InputDialog.Ask(this, "Import a frame",
            "Paste a link to a frame image (transparent PNG), or leave blank to pick a file on your PC.");
        try
        {
            string name;
            if (url != null)
            {
                if (!ImageIntake.IsHttpUrl(url)) { Status = "That doesn't look like a web link."; return; }
                Status = "Downloading frame…";
                name = await TemplateImporter.CreateFromUrlAsync("", url);
            }
            else
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Choose a frame image (transparent PNG where the art shows through)",
                    Filter = "Images|*.png;*.webp;*.gif;*.bmp|All files|*.*",
                };
                if (dlg.ShowDialog() != true) return;
                name = TemplateImporter.CreateFromFile(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
            }
            RefreshTemplates(name);
            Status = $"Imported frame as template \"{name}\". Tune its regions in CardinatorData/templates.";
        }
        catch (Exception ex)
        {
            Status = "Couldn't import frame: " + ex.Message;
        }
    }

    /// <summary>Opens the frame-design editor for the selected template; regenerates + re-renders on Apply.</summary>
    private void OnFrameDesign(object sender, RoutedEventArgs e)
    {
        var t = _selectedTemplate ?? Templates.FirstOrDefault();
        if (t == null) { Status = "No frame selected to design."; return; }
        try
        {
            var win = new FrameDesignWindow(t) { Owner = this };
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
        var dlg = new OpenFolderDialog { Title = "Choose a folder of art images to match by name" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int matched = ArtMatcher.MatchInto(Cards, dlg.FolderName, overwrite: false);
            _artBaseDir = dlg.FolderName;
            RenderPreview();
            Status = matched > 0 ? $"Matched art for {matched} card(s)." : "No filename matches found.";
        }
        catch (Exception ex)
        {
            Status = "Art match failed: " + ex.Message;
        }
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
        if (_selectedCard == null) return;
        var query = _selectedCard.Name;
        if (string.IsNullOrWhiteSpace(query)) { Status = "Type a card name first."; return; }

        Status = $"Looking up \"{query}\" on Scryfall…";
        try
        {
            var faces = await _scryfall.LookupFacesAsync(query);
            if (faces.Count == 0) { Status = $"No card found for \"{query}\"."; return; }

            var f = faces[0];
            _selectedCard.Name = f.Name;
            _selectedCard.ManaCost = f.ManaCost;
            _selectedCard.TypeLine = f.TypeLine;
            _selectedCard.RulesText = f.RulesText;
            _selectedCard.Power = f.Power;
            _selectedCard.Toughness = f.Toughness;
            _selectedCard.Loyalty = f.Loyalty;
            _selectedCard.SetCode = f.SetCode;
            _selectedCard.CollectorNumber = f.CollectorNumber;
            _selectedCard.Rarity = f.Rarity;

            // Double-faced: add the back as a new card in the project.
            for (int i = 1; i < faces.Count; i++)
            {
                faces[i].TemplateName = _selectedCard.TemplateName;
                Cards.Add(faces[i]);
            }

            _ = _symbols.PrimeAsync(faces.SelectMany(x => ManaText.SymbolTokens(x.ManaCost, x.RulesText)));

            // Pull the real art from Scryfall for any face that doesn't already have art.
            await FillArtFromScryfall(_selectedCard, f.ArtUrl);
            for (int i = 1; i < faces.Count; i++)
                await FillArtFromScryfall(faces[i], faces[i].ArtUrl);

            Status = faces.Count > 1
                ? $"Loaded \"{f.Name}\" (+{faces.Count - 1} back face) from Scryfall."
                : $"Loaded \"{f.Name}\" from Scryfall.";
        }
        catch (ScryfallException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = "Lookup failed: " + ex.Message;
        }
    }

    /// <summary>Downloads Scryfall art for a card that has none yet (best-effort; ignored on failure).</summary>
    private static async Task FillArtFromScryfall(CardModel card, string artUrl)
    {
        if (!string.IsNullOrWhiteSpace(card.ArtPath) || string.IsNullOrWhiteSpace(artUrl)) return;
        try { card.ArtPath = await ImageIntake.DownloadAsync(artUrl); }
        catch { /* keep the card without art rather than failing the lookup */ }
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

    /// <summary>Points the selected card at a new art file and resets its pan/zoom.</summary>
    private void SetArt(string path)
    {
        if (_selectedCard == null) return;
        _selectedCard.ArtPath = path;
        _selectedCard.ArtScale = 1.0;
        _selectedCard.ArtOffsetX = 0;
        _selectedCard.ArtOffsetY = 0;
        Status = "Loaded art: " + Path.GetFileName(path);
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
            InitialDirectory = AppPaths.OutputDir,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bmp = _renderer.RenderToBitmap(_selectedCard, template, supersample: 2);
            CardExporter.Save(bmp, dlg.FileName);   // PNG or JPEG by extension
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

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
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
    }

    private void OnPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isPanning = false;
        PreviewImageControl.ReleaseMouseCapture();
    }

    private void OnPreviewWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_selectedCard == null || string.IsNullOrWhiteSpace(_selectedCard.ArtPath)) return;
        _selectedCard.ArtScale = Clamp(_selectedCard.ArtScale + e.Delta / 120.0 * 0.08, 0.5, 3.0);
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
        try { Process.Start("explorer.exe", AppPaths.OutputDir); }
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
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bmp = System.Windows.Clipboard.GetImage();
                if (bmp != null) { SetArt(ImageIntake.SaveBitmap(bmp)); return; }
            }
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
