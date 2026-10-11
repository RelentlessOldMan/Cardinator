using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Command-line work on saved sets, for scripting the same steps the window does: fetch a Moxfield deck, import a
/// deck into a set, render a set, change its frame or art, and describe it. Each goes through the code the window
/// uses — the deck import's parser and Scryfall fill, <see cref="CardProject.LoadResolved"/> to open a set and
/// <see cref="ProjectWriter"/> to save one (art copied into the set, the previous file backed up, atomic write) —
/// so a set made here opens in the app exactly as if it had been made there. Each returns a process exit code:
/// 0 done, 1 failed, 2 bad arguments, 3 nothing to do.
/// </summary>
public static class SetCommands
{
    /// <summary>Stands in for the Moxfield fetch in tests (deck id → deck JSON).</summary>
    internal static Func<string, Task<string>>? TestFetch;

    private static readonly Regex BareDeckId = new(@"^[A-Za-z0-9_-]{10,40}$");

    /// <summary>The deck id in a Moxfield link, or a bare deck id itself; null if it is neither.</summary>
    internal static string? DeckId(string source) =>
        MoxfieldClient.ExtractDeckId(source)
        ?? (!File.Exists(source) && BareDeckId.IsMatch(source.Trim()) ? source.Trim() : null);

    /// <summary>
    /// --moxfield: fetches a Moxfield deck and writes it to <paramref name="outPath"/> — the deck's JSON when the
    /// name ends in .json, else the plain decklist the app's import box shows ("1 Sol Ring (LTC) 273" per line).
    /// </summary>
    public static int RunMoxfield(string source, string outPath)
    {
        var id = DeckId(source);
        if (id == null) { Console.Error.WriteLine($"Moxfield: '{source}' isn't a Moxfield deck link or id."); return 2; }
        try
        {
            var json = FetchDeck(id);
            var text = outPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? json : MoxfieldClient.ToDeckList(json);
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            IoUtil.AtomicWriteText(outPath, text);
            var (name, cards) = MoxfieldClient.ParseDeck(json, "");
            Console.WriteLine($"Wrote {outPath}: “{name}”, {cards.Count} card(s).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Moxfield FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// --import: makes a set from a deck — a Moxfield link/id, or a list/CSV file — as the window's deck import
    /// does: parse, fill blanks and art from Scryfall, then save. Options: frame=&lt;name&gt; (the set's house frame, and
    /// the frame of every card the list doesn't give one), art=&lt;dir&gt; (your own pictures, matched to cards by file name, used over Scryfall's),
    /// noart (don't download Scryfall art), append (add to an existing set, skipping names it already has; without
    /// it an existing set is never touched).
    /// </summary>
    public static int RunImport(string source, string setPath, string[] opts)
    {
        try
        {
            bool append = Has(opts, "append"), noArt = Has(opts, "noart");
            var frame = Opt(opts, "frame");
            var artDir = Opt(opts, "art");
            if (artDir != null && !Directory.Exists(artDir)) { Console.Error.WriteLine($"Import: art folder '{artDir}' not found."); return 2; }
            if (File.Exists(setPath) && !append)
            {
                Console.Error.WriteLine($"Import: {setPath} already exists. Pass append to add to it, or choose another file.");
                return 2;
            }

            // The deck as text, through the one parser every import uses.
            string content, listDir = "";
            if (DeckId(source) is { } id)
            {
                Console.WriteLine("Fetching the deck from Moxfield…");
                content = MoxfieldClient.ToDeckList(FetchDeck(id));
            }
            else if (File.Exists(source))
            {
                content = ImportService.ReadListFile(source);
                listDir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? "";
            }
            else { Console.Error.WriteLine($"Import: '{source}' is neither a Moxfield deck nor a file."); return 2; }

            var templates = new TemplateService().LoadAll();
            CardProject? existing = append && File.Exists(setPath) ? CardProject.LoadResolved(setPath, out _) : null;
            var houseFrame = frame ?? (existing?.DefaultTemplate is { Length: > 0 } d ? d : templates.FirstOrDefault()?.Name ?? "");
            if (frame != null && templates.All(t => t.Name != frame))
                Console.WriteLine($"Note: frame “{frame}” isn't installed — the cards keep its name, but draw on another until it is.");

            var parsed = ImportService.ParseWithReport(content, listDir, houseFrame);
            var imported = parsed.Cards;
            if (existing != null) imported = ImportService.RemoveNamesAlreadyIn(existing.Cards, imported);
            if (imported.Count == 0)
            {
                Console.Error.WriteLine(parsed.Cards.Count == 0 ? "Import: no cards found." : "Import: every card is already in the set.");
                return 3;
            }
            int skippedDupes = parsed.Cards.Count - imported.Count;

            var profile = existing?.Profile ?? new SetProfile();
            foreach (var item in imported) profile.ApplyDefaults(item.Card);   // inherit the set's shared details
            Console.WriteLine($"Read {imported.Count} card(s); filling from Scryfall…");
            var report = Task.Run(() => BatchService.FillFromScryfallAsync(imported, new Progress<string>(Console.WriteLine), downloadArt: !noArt))
                             .GetAwaiter().GetResult();
            foreach (var item in imported)
                if (item.Card.BackFace is { } back) profile.ApplyDefaults(back);   // backs the lookup attached

            var added = imported.Select(i => i.Card).ToList();
            int matched = artDir != null ? MatchArt(added, artDir) : 0;

            var cards = (existing?.Cards ?? new List<CardModel>()).Concat(added).ToList();
            var name = existing?.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(setPath);
            var dir = Path.GetDirectoryName(Path.GetFullPath(setPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            int stranded = ProjectWriter.Write(setPath, cards, name, existing?.ArtBaseDir ?? listDir, houseFrame, profile);

            Console.WriteLine($"Saved {setPath}: {added.Count} card(s) added ({cards.Count} in the set). "
                + $"Scryfall filled {report.Filled}"
                + (report.NotFound.Count > 0 ? $"; not found: {string.Join(", ", report.NotFound)}" : "")
                + (artDir != null ? $"; {matched} picture(s) from {artDir}" : "")
                + (skippedDupes > 0 ? $"; {skippedDupes} already in the set" : "")
                + (parsed.Skipped > 0 ? $"; {parsed.Skipped} unreadable line(s)" : "")
                + (stranded > 0 ? $"; {stranded} picture(s) couldn't be copied into the set" : "") + ".");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Import FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// --renderset: renders a saved set — each card on its own frame, a card whose frame isn't installed on the
    /// set's house frame, as the app's export does (NNN_name.png, backs as -back.png). Options: sheet (3x3
    /// printable pages instead), a4, templates=&lt;dir&gt; (also use frames straight from a folder, uninstalled; they
    /// win over installed frames of the same name), only=&lt;name|name…&gt; (just the cards whose name contains one of
    /// these), nosym (the app's own mana pips; no Scryfall symbol download).
    /// </summary>
    public static int RunRenderSet(string setPath, string outDir, string[] opts)
    {
        try
        {
            var project = CardProject.LoadResolved(setPath, out _);
            var cards = Only(project.Cards, Opt(opts, "only"));
            if (cards.Count == 0) { Console.Error.WriteLine("RenderSet: no cards to render."); return 3; }
            var templates = Templates(Opt(opts, "templates"));
            if (templates.Count == 0) { Console.Error.WriteLine("RenderSet: no frames available."); return 2; }
            var missing = FramesUsed(cards).Where(f => templates.All(t => t.Name != f)).ToList();
            if (missing.Count > 0) Console.WriteLine($"Not installed (drawn on the house frame): {string.Join(", ", missing)}");

            var symbols = new SymbolService();
            if (!Has(opts, "nosym"))
            {
                var tokens = cards.SelectMany(c => c.Parts()).SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
                Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();
            }

            if (Has(opts, "sheet"))
            {
                var page = Has(opts, "a4") ? PageSpec.A4 : PageSpec.Letter;
                var pages = SheetExporter.Compose(BatchService.ExpandFaces(cards), templates, symbols, page, houseFrame: project.DefaultTemplate);
                var paths = SheetExporter.Save(pages, outDir);
                Console.WriteLine($"Rendered {cards.Count} card(s) on {paths.Count} sheet page(s) to {outDir}.");
                return 0;
            }
            var result = BatchService.ExportAll(cards, templates, outDir, symbols, houseFrame: project.DefaultTemplate);
            foreach (var err in result.Errors) Console.Error.WriteLine("  ! " + err);
            Console.WriteLine($"Rendered {result.Exported}/{cards.Count - result.Repeats} card(s) to {outDir}"
                + (result.Repeats > 0 ? $" ({result.Repeats} repeated card(s) rendered once)." : "."));
            return result.Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RenderSet FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// --restyle: changes a saved set in place (the previous file is backed up first, as on every save). Options:
    /// frame=&lt;name&gt; (the cards' frame; for the whole set also its house frame), art=&lt;dir&gt; (pictures matched to
    /// cards by file name replace their art; cards with no match keep theirs), titles=&lt;file&gt; (retitle cards: each line is
    /// "card name&lt;TAB&gt;new title"; the card shows the new title with its real name as the subtitle — for flavor-named
    /// cards like Universes Beyond's), setcode=&lt;code&gt; and number=&lt;n&gt; (the footer's set code and collector number —
    /// a custom set's own instead of the real printing's), symbol=&lt;image&gt; (the set symbol drawn on the type line),
    /// only=&lt;name|name…&gt; (just those cards). Art is matched (by the names the cards have) before retitling, so give art
    /// and titles together, or art before titles.
    /// </summary>
    public static int RunRestyle(string setPath, string[] opts)
    {
        try
        {
            var frame = Opt(opts, "frame");
            var artDir = Opt(opts, "art");
            var only = Opt(opts, "only");
            var titlesFile = Opt(opts, "titles");
            var setCode = Opt(opts, "setcode");
            var number = Opt(opts, "number");
            var symbol = Opt(opts, "symbol");
            if (frame == null && artDir == null && titlesFile == null && setCode == null && number == null && symbol == null)
            { Console.Error.WriteLine("Restyle: give frame=, art=, titles=, setcode=, number= and/or symbol=."); return 2; }
            if (symbol != null && !File.Exists(symbol)) { Console.Error.WriteLine($"Restyle: symbol image '{symbol}' not found."); return 2; }
            if (titlesFile != null && !File.Exists(titlesFile)) { Console.Error.WriteLine($"Restyle: titles file '{titlesFile}' not found."); return 2; }
            if (artDir != null && !Directory.Exists(artDir)) { Console.Error.WriteLine($"Restyle: art folder '{artDir}' not found."); return 2; }

            var project = CardProject.LoadResolved(setPath, out _);
            var cards = Only(project.Cards, only);
            if (cards.Count == 0) { Console.Error.WriteLine("Restyle: no cards matched."); return 3; }

            if (frame != null)
            {
                foreach (var c in cards) SetFrame(c, frame);
                if (only == null) project.DefaultTemplate = frame;
                if (new TemplateService().LoadAll().All(t => t.Name != frame))
                    Console.WriteLine($"Note: frame “{frame}” isn't installed — the cards keep its name, but draw on another until it is.");
            }
            int matched = artDir != null ? MatchArt(cards, artDir) : 0;
            int retitled = titlesFile != null ? Retitle(cards, File.ReadAllLines(titlesFile)) : 0;
            foreach (var face in cards.SelectMany(c => c.Parts()))
            {
                if (setCode != null) face.SetCode = setCode;
                if (number != null) face.CollectorNumber = number;
                if (symbol != null) face.SetSymbolPath = Path.GetFullPath(symbol);
            }

            ProjectWriter.Write(setPath, project.Cards, project.Name, project.ArtBaseDir, project.DefaultTemplate, project.Profile);
            Console.WriteLine($"Saved {setPath}: {cards.Count} card(s)"
                + (frame != null ? $" on “{frame}”" : "")
                + (artDir != null ? $"; {matched} picture(s) from {artDir}" : "")
                + (titlesFile != null ? $"; {retitled} retitled" : "")
                + (setCode != null || number != null ? $"; footer {number ?? "(number kept)"} {setCode ?? "(set kept)"}" : "")
                + (symbol != null ? $"; set symbol {Path.GetFileName(symbol)}" : "") + ".");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Restyle FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>--info: describes a saved set — its house frame, and per card its frame and whether its art is
    /// there — one tab-separated line per card, so it reads in a terminal and parses in a script.</summary>
    public static int RunInfo(string setPath, string[] opts)
    {
        try
        {
            var project = CardProject.LoadResolved(setPath, out var folder);
            var installed = Templates(Opt(opts, "templates")).Select(t => t.Name).ToHashSet();
            string FrameOf(string? f) => string.IsNullOrEmpty(f) ? "-" : installed.Contains(f) ? f : f + " (not installed)";
            string ArtOf(CardModel c) => string.IsNullOrWhiteSpace(c.ArtPath) ? "none" : File.Exists(c.ArtPath) ? "ok" : "missing";

            Console.WriteLine($"Set\t{project.Name}");
            Console.WriteLine($"Folder\t{folder}");
            Console.WriteLine($"House frame\t{FrameOf(project.DefaultTemplate)}");
            Console.WriteLine($"Cards\t{project.Cards.Count}");
            Console.WriteLine("#\tName\tFrame\tArt");
            for (int i = 0; i < project.Cards.Count; i++)
            {
                var c = project.Cards[i];
                Console.WriteLine($"{i + 1}\t{c.Name}\t{FrameOf(c.TemplateName)}\t{ArtOf(c)}");
                if (c.BackFace is { } b) Console.WriteLine($"{i + 1}b\t{b.Name}\t{FrameOf(b.TemplateName)}\t{ArtOf(b)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Info FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>--listframes: the names of the installed frames (plus any in templates=&lt;dir&gt;), one per line —
    /// what frame= takes.</summary>
    public static int RunListFrames(string[] opts)
    {
        foreach (var t in Templates(Opt(opts, "templates")).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(t.Name);
        return 0;
    }

    // --- shared -------------------------------------------------------------

    /// <summary>A card's frame, and its back's when the back named one of its own (a back with none follows its front).</summary>
    private static void SetFrame(CardModel card, string frame)
    {
        card.TemplateName = frame;
        if (card.BackFace is { } back && !string.IsNullOrEmpty(back.TemplateName)) back.TemplateName = frame;
    }

    /// <summary>Gives each face (a double-faced card's back too, and a split card's other half) the picture in
    /// <paramref name="dir"/> named after it; faces with no match keep their art. Returns how many got one.</summary>
    private static int MatchArt(IEnumerable<CardModel> cards, string dir) =>
        ArtMatcher.MatchInto(cards.SelectMany(c => c.Parts()).ToList(), dir, overwrite: true);

    /// <summary>
    /// Retitles faces from "card name&lt;TAB&gt;new title" lines: the face takes the new title and keeps its real name as the
    /// subtitle. A face already retitled is found by that subtitle, so running it again (or with new titles) is safe.
    /// Its rules text names it by the new title too ("Whenever Ulamog attacks" → "Whenever Adam attacks"), like
    /// Universes Beyond cards do. Blank lines and # comments are skipped. Returns how many faces were retitled.
    /// </summary>
    internal static int Retitle(IEnumerable<CardModel> cards, IEnumerable<string> lines)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var parts = line.Split('	');
            if (parts.Length >= 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0) titles[parts[0].Trim()] = parts[1].Trim();
        }
        int n = 0;
        foreach (var face in cards.SelectMany(c => c.Parts()))
        {
            var real = titles.ContainsKey(face.Name) ? face.Name : face.Subtitle;
            if (string.IsNullOrEmpty(real) || !titles.TryGetValue(real, out var title)) continue;
            face.RulesText = RenameInText(face.RulesText, title, real, face.Name);
            face.Name = title;
            face.Subtitle = real;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Replaces a card's references to itself in <paramref name="text"/> with <paramref name="title"/>: its full name, and
    /// for a name with a comma ("Ulamog, the Ceaseless Hunger") the short name rules text uses ("Ulamog"), which becomes the
    /// title's short name ("Adam" of "Adam, the First Man"). Whole words only; one pass, so a replacement isn't replaced again.
    /// </summary>
    internal static string RenameInText(string text, string title, params string[] oldNames)
    {
        if (string.IsNullOrEmpty(text)) return text;
        static string? Short(string name) { int i = name.IndexOf(", ", StringComparison.Ordinal); return i > 0 ? name[..i] : null; }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var old in oldNames.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            map.TryAdd(old, title);
            if (Short(old) is { } s) map.TryAdd(s, Short(title) ?? title);
        }
        var pattern = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return Regex.Replace(text, $@"(?<!\w)(?:{pattern})(?!\w)", m => map[m.Value]);
    }

    /// <summary>The installed frames, with those in <paramref name="extraDir"/> first so they win a name clash.</summary>
    private static List<Template> Templates(string? extraDir)
    {
        var service = new TemplateService();
        var list = new List<Template>();
        if (!string.IsNullOrWhiteSpace(extraDir)) list.AddRange(service.LoadFrom(extraDir));
        list.AddRange(service.LoadAll().Where(t => list.All(x => x.Name != t.Name)));
        return list;
    }

    private static IEnumerable<string> FramesUsed(IEnumerable<CardModel> cards) =>
        cards.SelectMany(c => c.Parts()).Select(c => c.TemplateName).Where(n => !string.IsNullOrEmpty(n)).Distinct();

    /// <summary>The cards whose name (or subtitle) contains one of the |-separated parts (case-insensitive); all of them for none.
    /// | because card names have commas in them.</summary>
    internal static List<CardModel> Only(IEnumerable<CardModel> cards, string? only)
    {
        if (string.IsNullOrWhiteSpace(only)) return cards.ToList();
        var parts = only.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cards.Where(c => parts.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)
                                             || c.Subtitle.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();   // a retitled card by its real name too
    }

    /// <summary>
    /// The Moxfield fetch, which needs the window's embedded browser: it has to run on this (UI) thread with the
    /// dispatcher pumping, so the command waits in a nested dispatcher frame rather than blocking the thread.
    /// </summary>
    private static string FetchDeck(string deckId)
    {
        if (TestFetch is { } fake) return fake(deckId).GetAwaiter().GetResult();
        // The fetch's hidden browser window is the only window: closing it would end the app (and this command,
        // with exit code 0) unless shutting down is left to the command.
        if (System.Windows.Application.Current is { } app) app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        string? json = null;
        Exception? error = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { json = await MoxfieldFetcher.FetchDeckJsonAsync(deckId, new Progress<string>(Console.WriteLine)); }
            catch (Exception ex) { error = ex; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (error != null) throw error;
        return json!;
    }

    private static bool Has(string[] opts, string flag) => opts.Any(o => o.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? Opt(string[] opts, string name)
    {
        var hit = opts.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return hit?[(name.Length + 1)..].Trim('"') is { Length: > 0 } v ? v : null;
    }
}
