using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Headless render check: loads templates, renders each sample card to a PNG (at 2x),
/// and writes them to an output folder. No window is shown. Returns 0 on success.
/// </summary>
public static class SelfTest
{
    public static int Run(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var templates = new TemplateService().LoadAll();
            if (templates.Count == 0)
            {
                Console.Error.WriteLine("SelfTest: no templates found.");
                return 2;
            }

            var symbols = new SymbolService();
            var template = templates[0];
            var sampleCards = SampleCards.All(template.Name);
            var tokens = sampleCards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();
            var renderer = new CardRenderer(symbols);

            int i = 0;
            foreach (var card in sampleCards)
            {
                var bmp = renderer.RenderToBitmap(card, template, supersample: 2);
                var path = Path.Combine(outDir, $"selftest_{i:00}_{Slug(card.Name)}.png");
                CardExporter.SavePng(bmp, path);
                Console.WriteLine($"Wrote {path} ({bmp.PixelWidth}x{bmp.PixelHeight})");
                i++;
            }

            Console.WriteLine($"SelfTest OK: {i} card(s) rendered with template '{template.Name}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SelfTest FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Fetches a real card from Scryfall, prints its fields, and renders it to a PNG.
    /// Verifies the Scryfall -> render path headlessly. Returns 0 on success.
    /// </summary>
    public static int RunLookup(string name, string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            // Run off the UI thread to avoid the STA SynchronizationContext deadlocking on the await.
            var faces = Task.Run(() => new ScryfallClient().LookupFacesAsync(name)).GetAwaiter().GetResult();
            if (faces.Count == 0)
            {
                Console.Error.WriteLine($"Lookup: no card found for \"{name}\".");
                return 3;
            }

            var templates = new TemplateService().LoadAll();
            var template = templates[0];
            var symbols = new SymbolService();
            var tokens = faces.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();
            var renderer = new CardRenderer(symbols);

            for (int f = 0; f < faces.Count; f++)
            {
                var card = faces[f];
                card.TemplateName = template.Name;
                Console.WriteLine($"Face {f + 1}: {card.Name} | {card.ManaCost} | {card.TypeLine}"
                    + (string.IsNullOrEmpty(card.Loyalty) ? "" : $" | loyalty {card.Loyalty}"));

                // Pull the real card art from Scryfall so the lookup renders with artwork.
                if (!string.IsNullOrWhiteSpace(card.ArtUrl))
                {
                    try
                    {
                        card.ArtPath = Task.Run(() => ImageIntake.DownloadAsync(card.ArtUrl)).GetAwaiter().GetResult();
                        Console.WriteLine($"  art: {Path.GetFileName(card.ArtPath)}");
                    }
                    catch (Exception ex) { Console.WriteLine("  art download failed: " + ex.Message); }
                }

                var bmp = renderer.RenderToBitmap(card, template, supersample: 2);
                var suffix = faces.Count > 1 ? $"_face{f + 1}" : "";
                var path = Path.Combine(outDir, $"lookup_{Slug(card.Name)}{suffix}.png");
                CardExporter.SavePng(bmp, path);
                Console.WriteLine($"Wrote {path} ({bmp.PixelWidth}x{bmp.PixelHeight})");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Lookup FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Batch pipeline: parse a name-list / CSV file, fill blanks from Scryfall, render all to PNGs.
    /// Verifies the whole many-cards path headlessly. Returns 0 on success.
    /// </summary>
    public static int RunBatch(string inputPath, string outDir, string? artDir)
    {
        try
        {
            var content = File.ReadAllText(inputPath);
            var templates = new TemplateService().LoadAll();
            var defaultTemplate = templates[0].Name;

            var cards = ImportService.Parse(content, artDir, defaultTemplate);
            Console.WriteLine($"Parsed {cards.Count} card(s) from {Path.GetFileName(inputPath)}.");
            if (cards.Count == 0) return 3;

            var progress = new Progress<string>(Console.WriteLine);
            var report = Task.Run(() => BatchService.FillFromScryfallAsync(cards, progress))
                             .GetAwaiter().GetResult();
            if (report.NotFound.Count > 0)
                Console.WriteLine("Not found: " + string.Join(", ", report.NotFound));

            // ExportAll renders each card's back face itself (writes -back.png), so no flattening here.
            var models = cards.Select(c => c.Card).ToList();
            int dfcCount = models.Count(c => c.IsDoubleFaced);
            if (dfcCount > 0) Console.WriteLine($"{dfcCount} double-faced card(s) — backs export as -back.png.");

            if (!string.IsNullOrWhiteSpace(artDir))
            {
                int m = ArtMatcher.MatchInto(models, artDir!, overwrite: false);
                Console.WriteLine($"Art match: {m} card(s) matched from {artDir}.");
            }

            var symbols = new SymbolService();
            var tokens = models.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();
            var result = BatchService.ExportAll(models, templates, outDir, symbols, progress);
            foreach (var err in result.Errors) Console.Error.WriteLine("  ! " + err);

            Console.WriteLine($"Batch done: {result.Exported}/{models.Count} exported to {outDir}.");
            return result.Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Batch FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Parses a list/CSV, fills from Scryfall, matches art, and composes printable 3x3 sheets
    /// (PNG per page). Verifies the sheet pipeline headlessly. Returns 0 on success.
    /// </summary>
    public static int RunSheet(string inputPath, string outDir, string? artDir, bool a4)
    {
        try
        {
            var content = File.ReadAllText(inputPath);
            var templates = new TemplateService().LoadAll();
            var cards = ImportService.Parse(content, artDir, templates[0].Name);
            Console.WriteLine($"Parsed {cards.Count} card(s).");
            if (cards.Count == 0) return 3;

            var progress = new Progress<string>(Console.WriteLine);
            var report = Task.Run(() => BatchService.FillFromScryfallAsync(cards, progress)).GetAwaiter().GetResult();
            if (report.NotFound.Count > 0) Console.WriteLine("Not found: " + string.Join(", ", report.NotFound));

            // Single-sided sheet: expand double-faced cards so both faces get their own printable slot.
            var models = BatchService.ExpandFaces(cards.Select(c => c.Card).ToList());
            if (!string.IsNullOrWhiteSpace(artDir))
                Console.WriteLine($"Art match: {ArtMatcher.MatchInto(models, artDir!, false)} card(s).");

            var symbols = new SymbolService();
            var tokens = models.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();

            var page = a4 ? PageSpec.A4 : PageSpec.Letter;
            var pages = SheetExporter.Compose(models, templates, symbols, page, progress);
            var paths = SheetExporter.Save(pages, outDir);
            Console.WriteLine($"Sheet done: {paths.Count} page(s) ({page.Cols}x{page.Rows}) to {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Sheet FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>Renders a saved card JSON to a PNG (CLI export). Returns 0 on success.</summary>
    public static int RunRender(string cardJsonPath, string outPath, string[]? opts = null)
    {
        try
        {
            opts ??= Array.Empty<string>();
            int scale = OptInt(opts, "scale", 2);
            double dpi = OptInt(opts, "dpi", 0);
            int bleed = OptInt(opts, "bleed", 0);
            int quality = OptInt(opts, "q", 92);

            bool noSym = opts.Any(o => o.Equals("nosym", StringComparison.OrdinalIgnoreCase));

            var card = Cardinator.Models.CardModel.Load(cardJsonPath);
            var templates = new TemplateService().LoadAll();
            var template = templates.FirstOrDefault(t => t.Name == card.TemplateName) ?? templates[0];
            var symbols = new SymbolService();
            if (!noSym)   // nosym = don't fetch Scryfall symbols; use the app's generic pips instead
                Task.Run(() => symbols.PrimeAsync(ManaText.SymbolTokens(card.ManaCost, card.RulesText))).GetAwaiter().GetResult();

            var bmp = new CardRenderer(symbols).RenderToBitmap(card, template, Math.Clamp(scale, 1, 6));
            if (bleed > 0) bmp = PrintExporter.AddBleed(bmp, bleed * Math.Clamp(scale, 1, 6));
            if (dpi > 0) bmp = CardExporter.StampDpi(bmp, dpi);
            CardExporter.Save(bmp, outPath, quality);

            Console.WriteLine($"Wrote {outPath} ({bmp.PixelWidth}x{bmp.PixelHeight}) using template '{template.Name}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Render FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Batch-renders ready-made card JSONs to PNGs. <paramref name="inPath"/> is a single card.json or a
    /// folder of them (each *.json except template.json). Options: scale=N (supersample, default 2),
    /// templates=&lt;dir&gt; to also load custom frames straight from a folder (no install needed — they take
    /// precedence over installed frames of the same name), nosym to skip fetching Scryfall mana symbols.
    /// Each card is written to &lt;outDir&gt;/&lt;slug&gt;.png, resolving its templateName across the combined set.
    /// </summary>
    public static int RunRenderCards(string inPath, string outDir, string[]? opts = null)
    {
        try
        {
            opts ??= Array.Empty<string>();
            int scale = Math.Clamp(OptInt(opts, "scale", 2), 1, 6);
            bool noSym = opts.Any(o => o.Equals("nosym", StringComparison.OrdinalIgnoreCase));
            var extraDir = OptStr(opts, "templates");

            // Collect the card JSON files.
            List<string> cardFiles;
            if (File.Exists(inPath))
                cardFiles = new List<string> { inPath };
            else if (Directory.Exists(inPath))
                cardFiles = Directory.EnumerateFiles(inPath, "*.json")
                    .Where(f => !Path.GetFileName(f).Equals("template.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            else
            {
                Console.Error.WriteLine($"RenderCards: '{inPath}' is not a file or folder.");
                return 2;
            }
            if (cardFiles.Count == 0)
            {
                Console.Error.WriteLine($"RenderCards: no card JSON files found in '{inPath}'.");
                return 2;
            }

            // Templates: installed ones, plus any from templates=<dir> (the extras win on name clashes).
            var service = new TemplateService();
            var byName = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in service.LoadAll()) byName[t.Name] = t;
            if (!string.IsNullOrWhiteSpace(extraDir))
            {
                var extras = service.LoadFrom(extraDir);
                foreach (var t in extras) byName[t.Name] = t;
                Console.WriteLine($"Loaded {extras.Count} custom frame(s) from {extraDir}.");
            }
            if (byName.Count == 0) { Console.Error.WriteLine("RenderCards: no templates available."); return 2; }
            var fallback = byName.Values.OrderBy(t => t.Name, StringComparer.Ordinal).First();

            // Load all cards, then prime every mana symbol they use in one pass.
            var cards = new List<Cardinator.Models.CardModel>();
            foreach (var f in cardFiles)
            {
                try { cards.Add(Cardinator.Models.CardModel.Load(f)); }
                catch (Exception ex) { Console.Error.WriteLine($"  skip {Path.GetFileName(f)}: {ex.Message}"); }
            }
            if (cards.Count == 0) { Console.Error.WriteLine("RenderCards: no cards loaded."); return 2; }

            var symbols = new SymbolService();
            if (!noSym)
            {
                var tokens = cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
                Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();
            }

            Directory.CreateDirectory(outDir);
            var renderer = new CardRenderer(symbols);
            int ok = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                var tpl = (!string.IsNullOrEmpty(card.TemplateName) && byName.TryGetValue(card.TemplateName, out var hit))
                    ? hit : fallback;
                bool missing = !string.IsNullOrEmpty(card.TemplateName) && !byName.ContainsKey(card.TemplateName);

                // Unique output name even if two cards share a title.
                var baseName = Slug(string.IsNullOrWhiteSpace(card.Name) ? "card" : card.Name);
                var name = baseName;
                for (int i = 2; !usedNames.Add(name); i++) name = $"{baseName}-{i}";

                var outFile = Path.Combine(outDir, name + ".png");
                var bmp = renderer.RenderToBitmap(card, tpl, scale);
                CardExporter.SavePng(bmp, outFile);
                ok++;
                Console.WriteLine($"  {card.Name}  ->  {name}.png  [{tpl.Name}]"
                    + (missing ? $"  (templateName '{card.TemplateName}' not found — used fallback)" : ""));
            }

            Console.WriteLine($"Rendered {ok}/{cards.Count} card(s) to {outDir}.");
            return ok > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RenderCards FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Renders one card across every on/off permutation of the composable frame knobs (background,
    /// connected panels, taper, faded edges, regal topper) so the combinations can be eyeballed.
    /// </summary>
    public static int RunPermute(string cardJsonPath, string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var card = Cardinator.Models.CardModel.Load(cardJsonPath);
            var templates = new TemplateService().LoadAll();
            var baseTpl = templates.FirstOrDefault(t => t.Name == card.TemplateName) ?? templates[0];
            var specPath = Path.Combine(Path.GetDirectoryName(baseTpl.FramePath)!, "template.json");

            var symbols = new SymbolService();
            Task.Run(() => symbols.PrimeAsync(ManaText.SymbolTokens(card.ManaCost, card.RulesText))).GetAwaiter().GetResult();
            var renderer = new CardRenderer(symbols);

            var knobs = new (string label, Action<Cardinator.Models.TemplateSpec, bool> set)[]
            {
                ("bg",    (s, v) => s.PanelBackground = v),
                ("conn",  (s, v) => s.ConnectedPanels = v),
                ("taper", (s, v) => s.BottomTaper = v),
                ("faded", (s, v) => s.FadedEdges = v),
                ("regal", (s, v) => s.TopEmblem = v ? "regal" : "none"),
            };
            int n = knobs.Length, total = 1 << n;
            var tmpFrame = Path.Combine(Path.GetTempPath(), "cardinator-perm-frame.png");

            for (int mask = 0; mask < total; mask++)
            {
                var spec = Cardinator.Models.TemplateSpec.Load(specPath);
                spec.FrameStyle = "composable";
                var parts = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    bool on = (mask & (1 << i)) != 0;
                    knobs[i].set(spec, on);
                    if (on) parts.Add(knobs[i].label);
                }
                FrameGenerator.Generate(spec, tmpFrame);
                var img = LoadFrozen(tmpFrame);
                var tpl = new Template { Name = spec.Name, Spec = spec, FramePath = tmpFrame, FrameImage = img };
                var bmp = renderer.RenderToBitmap(card, tpl, 1);
                var label = parts.Count == 0 ? "plain" : string.Join("+", parts);
                CardExporter.Save(bmp, Path.Combine(outDir, $"perm_{mask:D2}_{label}.png"), 92);
                Console.WriteLine($"{mask:D2}  {label}");
            }
            Console.WriteLine($"Wrote {total} permutations to {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Permute FAILED: " + ex);
            return 1;
        }
    }

    private static System.Windows.Media.Imaging.BitmapImage LoadFrozen(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Reads an "name=value" integer option from the extra CLI args.</summary>
    private static int OptInt(string[] opts, string name, int fallback)
    {
        var hit = opts.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return hit != null && int.TryParse(hit[(name.Length + 1)..], out var v) ? v : fallback;
    }

    /// <summary>Reads a "name=value" option (value may be quoted); returns null if absent.</summary>
    private static string? OptStr(string[] opts, string name)
    {
        var hit = opts.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return hit?[(name.Length + 1)..].Trim('"');
    }

    /// <summary>
    /// Runs a Scryfall search, downloads each card's art, and renders the results — either as
    /// individual PNGs or (when <paramref name="sheet"/>) printable 3x3 sheet pages.
    /// </summary>
    public static int RunSearch(string query, string outDir, bool sheet, bool a4, int max)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var progress = new Progress<string>(Console.WriteLine);
            var cards = Task.Run(() => new ScryfallClient().SearchAsync(query, max, progress))
                            .GetAwaiter().GetResult().ToList();
            if (cards.Count == 0) { Console.Error.WriteLine($"No cards matched \"{query}\"."); return 3; }

            var templates = new TemplateService().LoadAll();
            var def = templates[0].Name;
            foreach (var c in cards)
                if (string.IsNullOrEmpty(c.TemplateName)) c.TemplateName = def;

            Console.WriteLine($"Downloading art for {cards.Count} card(s)…");
            foreach (var c in cards)
            {
                if (string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
                try { c.ArtPath = Task.Run(() => ImageIntake.DownloadAsync(c.ArtUrl)).GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"  art failed for {c.Name}: {ex.Message}"); }
            }

            var symbols = new SymbolService();
            var tokens = cards.SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();

            if (sheet)
            {
                var pages = SheetExporter.Compose(cards, templates, symbols, a4 ? PageSpec.A4 : PageSpec.Letter, progress);
                var paths = SheetExporter.Save(pages, outDir);
                Console.WriteLine($"Saved {paths.Count} sheet page(s) for {cards.Count} card(s) to {outDir}.");
            }
            else
            {
                var result = BatchService.ExportAll(cards, templates, outDir, symbols, progress);
                Console.WriteLine($"Exported {result.Exported}/{cards.Count} card(s) to {outDir}."
                    + (result.Errors.Count > 0 ? $" {result.Errors.Count} error(s)." : ""));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Search FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>Renders one sample card on every built-in frame (for a docs showcase). Returns 0.</summary>
    public static int RunFrames(string outDir, bool noSym)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            var card = new Cardinator.Models.CardModel
            {
                Name = "Frame Sample", ManaCost = "{2}{R}{W}", TypeLine = "Legendary Creature — Knight",
                RulesText = "First strike, vigilance\n{T}: Draw a card, then discard a card.",
                FlavorText = "One card, every frame.", Power = "3", Toughness = "3",
                SetCode = "CST", CollectorNumber = "1", Rarity = "M", Artist = "Cardinator Demo",
                ArtPath = "examples/01-custom-set/art/aria.png",
            };
            if (!noSym)
                Task.Run(() => symbols.PrimeAsync(ManaText.SymbolTokens(card.ManaCost, card.RulesText)))
                    .GetAwaiter().GetResult();

            var renderer = new CardRenderer(symbols);
            foreach (var t in templates)
            {
                card.TemplateName = t.Name;
                var bmp = renderer.RenderToBitmap(card, t, supersample: 1);
                var path = Path.Combine(outDir, Slug(t.Name) + ".png");
                CardExporter.SavePng(bmp, path);
                Console.WriteLine($"Wrote {path} ({t.Name})");
            }
            Console.WriteLine($"Rendered {templates.Count} frame(s) to {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Frames FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>Renders the decorative card back to a PNG. Returns 0 on success.</summary>
    public static int RunCardBack(string outPath, string? wordmark)
    {
        try
        {
            var bmp = BackRenderer.Render(string.IsNullOrWhiteSpace(wordmark) ? "CARDINATOR" : wordmark);
            CardExporter.Save(bmp, outPath);
            Console.WriteLine($"Wrote card back {outPath} ({bmp.PixelWidth}x{bmp.PixelHeight}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Card back FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>Creates a custom template from a frame image (local file or URL). Returns 0 on success.</summary>
    public static int RunNewTemplate(string name, string frameSource, bool fullArt)
    {
        try
        {
            string created = ImageIntake.IsHttpUrl(frameSource)
                ? Task.Run(() => TemplateImporter.CreateFromUrlAsync(name, frameSource, fullArt)).GetAwaiter().GetResult()
                : TemplateImporter.CreateFromFile(name, frameSource, fullArt);

            // Confirm it loads back as a usable template.
            var templates = new TemplateService().LoadAll();
            bool ok = templates.Any(t => t.Name == created);
            Console.WriteLine(ok
                ? $"Created template '{created}'{(fullArt ? " (full art)" : "")} and it loaded successfully."
                : $"Created template '{created}' but it did not load — check the frame image.");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("New template FAILED: " + ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------------ QA harness

    private sealed record QaResult(string Label, string Template, System.Windows.Media.Imaging.BitmapSource Bmp,
        List<ValidationIssue> Issues);

    /// <summary>
    /// Visual QA harness: renders a matrix of every card layout across the frames, runs the model-,
    /// geometry- and pixel-level checks on each, and writes labelled contact sheets plus a findings
    /// report. This is what replaces eyeballing every card by hand — regressions surface automatically.
    /// </summary>
    public static int RunQa(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var templates = new TemplateService().LoadAll();
            if (templates.Count == 0) { Console.Error.WriteLine("QA: no templates."); return 2; }

            var symbols = new SymbolService();
            var renderer = new CardRenderer(symbols);

            // Solid art so windows aren't blank (and the art-blank check has something to verify).
            var artPath = Path.Combine(outDir, "_qa_art.png");
            MakeSolidArt(artPath);

            var layoutCards = QaLayoutCards(artPath);
            var tokens = layoutCards.Select(c => c.card).SelectMany(c => ManaText.SymbolTokens(c.ManaCost, c.RulesText)).Distinct().ToList();
            Task.Run(() => symbols.PrimeAsync(tokens)).GetAwaiter().GetResult();

            var all = new List<CardModel>();
            all.AddRange(layoutCards.Select(c => c.card));

            var layoutResults = new List<QaResult>();
            foreach (var (card, tplName, label) in layoutCards)
            {
                var tpl = templates.FirstOrDefault(t => t.Name == tplName) ?? templates[0];
                layoutResults.Add(RenderAndInspect(renderer, card, tpl, label, all));
            }

            // Frame coverage: one standard creature on every installed frame.
            var frameResults = new List<QaResult>();
            int fn = 100;
            foreach (var tpl in templates)
            {
                var c = QaCreature(artPath);
                c.CollectorNumber = (fn++).ToString();   // unique so the duplicate check stays quiet
                c.TemplateName = tpl.Name;
                frameResults.Add(RenderAndInspect(renderer, c, tpl, tpl.Name, all));
            }

            BuildContactSheet(layoutResults, Path.Combine(outDir, "qa-layouts.png"), "Layout coverage");
            BuildContactSheet(frameResults, Path.Combine(outDir, "qa-frames.png"), "Frame coverage");

            var report = new List<string> { "CARDINATOR QA REPORT", "====================", "" };
            int errors = 0, warns = 0;
            foreach (var r in layoutResults.Concat(frameResults))
            {
                foreach (var i in r.Issues)
                {
                    if (i.Severity == IssueSeverity.Error) errors++;
                    else if (i.Severity == IssueSeverity.Warning) warns++;
                }
                if (r.Issues.Count > 0)
                {
                    report.Add($"{r.Label}  [{r.Template}]");
                    foreach (var i in r.Issues.OrderByDescending(x => x.Severity))
                        report.Add($"    {i}");
                    report.Add("");
                }
            }
            report.Insert(2, $"{layoutResults.Count + frameResults.Count} renders · {errors} error(s) · {warns} warning(s)");
            report.Insert(3, "");
            File.WriteAllText(Path.Combine(outDir, "QA-REPORT.txt"), string.Join("\n", report));

            Console.WriteLine($"QA: {layoutResults.Count + frameResults.Count} renders, {errors} error(s), {warns} warning(s).");
            Console.WriteLine($"Wrote qa-layouts.png, qa-frames.png, QA-REPORT.txt to {outDir}.");
            return errors > 0 ? 3 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("QA FAILED: " + ex); return 1; }
    }

    private static QaResult RenderAndInspect(CardRenderer renderer, CardModel card, Template tpl, string label, IReadOnlyCollection<CardModel> project)
    {
        card.TemplateName = tpl.Name;
        tpl = TemplateService.ResolveFor(card, tpl);   // judge the layout that's drawn, not the portrait source
        var bmp = renderer.RenderToBitmap(card, tpl, supersample: 1);
        bmp.Freeze();
        var issues = new List<ValidationIssue>();
        issues.AddRange(CardValidator.Validate(card, tpl.Spec, project));
        issues.AddRange(CardValidator.ValidateOtherHalf(card, tpl.Spec));
        issues.AddRange(RenderInspector.InspectCard(renderer, card, tpl, bmp));
        return new QaResult(label, tpl.Name, bmp, issues);
    }

    private static List<(CardModel card, string tplName, string label)> QaLayoutCards(string art)
    {
        int n = 0;
        CardModel C(string name, string mana, string type, string rules, string art2 = "",
            string pow = "", string tou = "", string loy = "", string flavor = "") => new()
        {
            Name = name, ManaCost = mana, TypeLine = type, RulesText = rules, FlavorText = flavor,
            Power = pow, Toughness = tou, Loyalty = loy, ArtPath = art2,
            SetCode = "QA", CollectorNumber = (++n).ToString(), Rarity = "R", Artist = "QA Harness",
        };
        return new()
        {
            (C("QA Creature", "{2}{G}", "Creature — Beast", "Trample\n{T}: Add {G}.", art, "3", "3", flavor: "A test of the wilds."), "Crimson Red", "Creature"),
            (C("QA Legend", "{1}{W}{B}", "Legendary Creature — Human Knight", "First strike, vigilance.", art, "2", "4"), "Gold Multicolor", "Legendary creature"),
            (C("QA Walker", "{3}{U}", "Legendary Planeswalker — Tester", "+1: Draw a card.\n-2: Return target creature to its owner's hand.\n-7: Take an extra turn.", art, loy: "4"), "Planeswalker", "Planeswalker"),
            (C("QA Saga", "{2}{G}", "Enchantment — Saga", "I, II — Search your library for a Forest.\nIII — Create a 5/5 Wurm.", art), "Showcase", "Saga"),
            (C("QA Class", "{1}{B}", "Enchantment — Class", "At the start, gain 1 life.\n{2}: Level 2\nEach opponent loses 1 life.", art), "Azure Modern", "Class"),
            (C("QA Leveler", "{W}", "Creature — Human Knight", "Level up {W} ({W}: Put a level counter on this. Level up only as a sorcery.)\nLEVEL 2-6\n3/3\nFirst strike\nLEVEL 7+\n4/4\nDouble strike", art, "1", "1"), "Gold Multicolor", "Level up"),
            (C("QA Prototype", "{7}", "Artifact Creature — Construct", "Prototype {2}{R} — 3/2 (You may cast this spell with different mana cost, color, and size. It keeps its abilities and types.)\nHaste", art, "6", "4"), "Slate Artifact", "Prototype"),
            (C("QA Mutant", "{3}{G}", "Creature — Beast", "Mutate {1}{G}{G} (If you cast this spell for its mutate cost, put it over or under target non-Human creature you own.)\nReach, trample", art, "4", "4"), "Midnight", "Mutate (dark text box)"),
            (C("QA Artifact", "{4}", "Legendary Artifact — Equipment", "Equipped creature gets +1/+1.\nEquip {2}", art, flavor: "Cold to the touch."), "Slate Artifact", "Artifact (no P/T)"),
            (C("QA Instant", "{1}{R}", "Instant", "Deal 3 damage to any target.", art, flavor: "Fast and bright."), "Ocean Blue", "Instant"),
            (C("QA Hybrid", "{2}{G/R}{G/R}", "Creature — Elemental", "({G/R} can be paid with either {G} or {R}.)", art, "4", "4"), "Forest Green", "Hybrid mana"),
            (C("QA Full Art", "{W}{U}{B}{R}{G}", "Legendary Creature — Avatar", "This spell can't be countered.", art, "7", "7"), "Full Art", "Full-art legend"),
            (WithDefense(C("QA Siege", "{2}{R}", "Battle — Siege", "(As a Siege enters, choose an opponent to protect it. You and others can attack it. When it's defeated, exile it, then cast it transformed.)\nWhen this enters, it deals 3 damage to any target.", art), "5"), "Crimson Red", "Battle (landscape)"),
            (C("QA Plane", "", "Plane — Testing Grounds", "Creatures you control get +1/+1.\nWhenever chaos ensues, draw a card.", art), "Full Art", "Plane (landscape, full art)"),
            (WithFlipHalf(C("QA Apprentice", "{1}{U}", "Creature — Human Wizard", "Whenever you cast your fourth spell each turn, flip this.", art, "1", "2")), "Ocean Blue", "Flip card"),
            (WithFlipHalf(C("QA Initiate", "{W}", "Creature — Human Monk", "When this deals combat damage, flip it.", art, "1", "1")), "Showcase", "Flip card (borderless)"),
            (WithFlipHalf(C("QA Steelhand", "{2}{W}", "Creature — Human Artificer", "Whenever an artifact enters, flip this.", art, "2", "2")), "Alchemist's Steel", "Flip card (picture frame's flip version)"),
            (WithSplitHalf(C("QA Wear", "{1}{R}", "Instant", "Destroy target artifact.\nFuse (You may cast one or both halves of this card from your hand.)", art)), "Ocean Blue", "Split card (Fuse)"),
            (WithSplitHalf(C("QA Pool", "{U}", "Enchantment — Room", "When you unlock this door, draw a card.\n(You may cast either half. That door unlocks on the battlefield.)", art)), "Showcase", "Split card (Room, borderless)"),
            (WithSplitHalf(C("QA Fire", "{1}{R}", "Instant", "Fire deals 2 damage divided as you choose among one or two targets.", art)), "Alchemist's Steel", "Split card (picture frame)"),
            (WithAftermath(C("QA Destined", "{1}{B}", "Instant", "Target creature gets +1/+0 and gains indestructible until end of turn.", art)), "Midnight", "Aftermath"),
            (WithAftermath(C("QA Fated", "{2}{G}", "Sorcery", "Search your library for a basic land card.", art)), "Sealed Gate", "Aftermath (picture frame's sideways version)"),
        };

        static CardModel WithDefense(CardModel c, string defense) { c.Defense = defense; return c; }
        static CardModel WithSplitHalf(CardModel c)
        {
            var shared = c.RulesText.Contains('\n') ? "\n" + c.RulesText[(c.RulesText.LastIndexOf('\n') + 1)..] : "";
            c.HalfLayout = "split";
            c.OtherHalf = new CardModel
            {
                Name = c.Name + " Too", ManaCost = "{2}{W}", TypeLine = c.TypeLine,
                RulesText = "Tap target creature. Draw a card." + shared, ArtPath = c.ArtPath,
            };
            return c;
        }
        static CardModel WithAftermath(CardModel c)
        {
            c.HalfLayout = "split";
            c.OtherHalf = new CardModel
            {
                Name = c.Name + " After", ManaCost = "{3}{G}", TypeLine = "Sorcery", ArtPath = c.ArtPath,
                RulesText = "Aftermath (Cast this spell only from your graveyard. Then exile it.)\nAll creatures able to block target creature this turn do so.",
            };
            return c;
        }
        static CardModel WithFlipHalf(CardModel c)
        {
            c.HalfLayout = "flip";
            c.OtherHalf = new CardModel
            {
                Name = c.Name + ", Ascended", TypeLine = "Legendary " + c.TypeLine,
                RulesText = "Flying\nWhenever an opponent casts a spell, draw a card.", Power = "3", Toughness = "4",
            };
            return c;
        }
    }

    private static CardModel QaCreature(string art) => new()
    {
        Name = "QA Creature", ManaCost = "{2}{R}", TypeLine = "Creature — Beast",
        RulesText = "Haste\nWhenever this attacks, it gets +1/+0.", FlavorText = "Every frame, one beast.",
        Power = "3", Toughness = "2", ArtPath = art, SetCode = "QA", CollectorNumber = "1", Rarity = "R", Artist = "QA Harness",
    };

    private static void MakeSolidArt(string path)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var g = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0x3A, 0x55, 0x74),
                System.Windows.Media.Color.FromRgb(0x8A, 0x5A, 0x3A),
                new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            dc.DrawRectangle(g, null, new System.Windows.Rect(0, 0, 900, 900));
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 255, 240, 200)),
                null, new System.Windows.Point(450, 380), 200, 200);
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        CardExporter.SavePng(rtb, path);
    }

    private static void BuildContactSheet(List<QaResult> items, string path, string title)
    {
        if (items.Count == 0) return;
        int cols = 4, rows = (items.Count + cols - 1) / cols;
        double tw = 250, th = tw * 1050 / 750, lab = 40, gap = 12, pad = 16, headH = 34;
        double W = pad * 2 + cols * tw + (cols - 1) * gap;
        double H = pad * 2 + headH + rows * (th + lab) + (rows - 1) * gap;

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 28)), null, new System.Windows.Rect(0, 0, W, H));
            dc.DrawText(Text(title + $"  —  {items.Count} renders", 20, System.Windows.Media.Colors.White, true), new System.Windows.Point(pad, 8));
            for (int i = 0; i < items.Count; i++)
            {
                int r = i / cols, c = i % cols;
                double x = pad + c * (tw + gap), y = pad + headH + r * (th + lab + gap);
                // Letterbox at the render's own aspect, so a landscape Battle/Plane isn't stretched tall.
                var bmp = items[i].Bmp;
                double fit = Math.Min(tw / bmp.PixelWidth, th / bmp.PixelHeight);
                double iw = bmp.PixelWidth * fit, ih = bmp.PixelHeight * fit;
                dc.DrawImage(bmp, new System.Windows.Rect(x + (tw - iw) / 2, y + (th - ih) / 2, iw, ih));
                var worst = items[i].Issues.Count == 0 ? IssueSeverity.Info : items[i].Issues.Max(z => z.Severity);
                var (bg, tag) = worst switch
                {
                    IssueSeverity.Error => (System.Windows.Media.Color.FromRgb(0xB0, 0x30, 0x28), "FAIL"),
                    IssueSeverity.Warning => (System.Windows.Media.Color.FromRgb(0xB0, 0x8A, 0x20), "WARN"),
                    _ => (System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x46), "PASS"),
                };
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(bg), null, new System.Windows.Rect(x, y + th, tw, lab));
                int nE = items[i].Issues.Count(z => z.Severity == IssueSeverity.Error);
                int nW = items[i].Issues.Count(z => z.Severity == IssueSeverity.Warning);
                string sub = nE + nW == 0 ? "" : $"  ({nE}E {nW}W)";
                dc.DrawText(Text($"{tag}{sub}", 13, System.Windows.Media.Colors.White, true), new System.Windows.Point(x + 6, y + th + 3));
                dc.DrawText(Text(items[i].Label, 12, System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xEE), false), new System.Windows.Point(x + 6, y + th + 20));
            }
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)W, (int)H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        CardExporter.SavePng(rtb, path);
    }

    private static System.Windows.Media.FormattedText Text(string s, double size, System.Windows.Media.Color color, bool bold) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                System.Windows.FontStyles.Normal, bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal),
            size, new System.Windows.Media.SolidColorBrush(color), 1.0);

    private static string Slug(string s) => TextUtil.Slug(s);
}
