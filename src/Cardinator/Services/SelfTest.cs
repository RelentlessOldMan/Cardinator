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
            var content = ImportService.ReadListFile(inputPath);
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

            Console.WriteLine($"Batch done: {result.Exported}/{models.Count - result.Repeats} exported to {outDir}"
                + (result.Repeats > 0 ? $" ({result.Repeats} repeated card(s) rendered once)." : "."));
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
            var content = ImportService.ReadListFile(inputPath);
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

    /// <summary>Whether a .json file is a saved set (an object with a "cards" list) rather than one card.</summary>
    private static bool LooksLikeSet(string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("cards", StringComparison.OrdinalIgnoreCase)
                                                              && p.Value.ValueKind == System.Text.Json.JsonValueKind.Array);
        }
        catch { return false; }   // not JSON at all: let the card loader report it
    }

    /// <summary>A card file's art path relative to the card file's own folder is resolved against it (it was read from
    /// the current folder and, not found there, drew an empty art window); art that still isn't there is warned about.</summary>
    private static void WarnMissingArt(Cardinator.Models.CardModel card, string cardFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(cardFile)) ?? "";
        foreach (var face in new[] { card, card.BackFace, card.OtherHalf })
        {
            if (face == null || string.IsNullOrWhiteSpace(face.ArtPath)) continue;
            if (!Path.IsPathRooted(face.ArtPath) && !File.Exists(face.ArtPath)
                && File.Exists(Path.Combine(dir, face.ArtPath)))
                face.ArtPath = Path.Combine(dir, face.ArtPath);
            if (!File.Exists(face.ArtPath))
                Console.Error.WriteLine($"  Note: {(string.IsNullOrWhiteSpace(face.Name) ? "a card" : face.Name)}'s art "
                    + $"\"{face.ArtPath}\" isn't there — its art window is drawn empty.");
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
            WarnMissingArt(card, cardJsonPath);
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
            if (!string.IsNullOrWhiteSpace(extraDir) && !Directory.Exists(extraDir))
            {
                Console.Error.WriteLine($"RenderCards: the templates folder '{extraDir}' isn't there.");
                return 2;
            }

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
            int skipped = 0;
            foreach (var f in cardFiles)
            {
                if (LooksLikeSet(f))   // a saved set beside the cards: not a card (it rendered blank)
                {
                    Console.WriteLine($"  skip {Path.GetFileName(f)}: it's a set, not a card (render it with --renderset).");
                    continue;
                }
                try { var c = Cardinator.Models.CardModel.Load(f); WarnMissingArt(c, f); cards.Add(c); }
                catch (Exception ex) { skipped++; Console.Error.WriteLine($"  skip {Path.GetFileName(f)}: {ex.Message}"); }
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

                // One card that fails to draw is reported and counted, and the rest are still rendered.
                try
                {
                    var outFile = Path.Combine(outDir, name + ".png");
                    var bmp = renderer.RenderToBitmap(card, tpl, scale);
                    CardExporter.SavePng(bmp, outFile);
                    Console.WriteLine($"  {card.Name}  ->  {name}.png  [{tpl.Name}]"
                        + (missing ? $"  (templateName '{card.TemplateName}' not found — used fallback)" : ""));
                    if (card.BackFace is { } back)   // a double-faced card's back, named like an export's
                    {
                        var backTpl = (!string.IsNullOrEmpty(back.TemplateName) && byName.TryGetValue(back.TemplateName, out var bt)) ? bt : tpl;
                        CardExporter.SavePng(renderer.RenderToBitmap(back, backTpl, scale), Path.Combine(outDir, name + "-back.png"));
                        Console.WriteLine($"  {back.Name}  ->  {name}-back.png  [{backTpl.Name}]");
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    Console.Error.WriteLine($"  {card.Name}: couldn't be drawn — {ex.Message}");
                }
            }

            Console.WriteLine($"Rendered {ok}/{cards.Count} card(s) to {outDir}."
                + (skipped > 0 ? $" {skipped} card(s) couldn't be read or drawn." : ""));
            return ok > 0 && skipped == 0 ? 0 : 1;   // a file that couldn't be read is an error, like --batch's
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
            WarnMissingArt(card, cardJsonPath);
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
            foreach (var c in cards) CardDetailsFill.TakeFrame(c, def);

            Console.WriteLine($"Downloading art for {cards.Count} card(s)…");
            foreach (var c in cards.SelectMany(c => new[] { c, c.BackFace }).OfType<CardModel>())   // DFC backs too
            {
                if (string.IsNullOrWhiteSpace(c.ArtUrl)) continue;
                try { c.ArtPath = Task.Run(() => ImageIntake.DownloadAsync(c.ArtUrl)).GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"  art failed for {c.Name}: {ex.Message}"); }
            }
            foreach (var c in cards) CardDetailsFill.SplitSharedArt(c);

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
                Console.WriteLine($"Exported {result.Exported}/{cards.Count - result.Repeats} card(s) to {outDir}."
                    + (result.Repeats > 0 ? $" {result.Repeats} repeated card(s) rendered once." : "")
                    + (result.Errors.Count > 0 ? $" {result.Errors.Count} error(s)." : ""));
                if (result.Errors.Count > 0) return 1;   // as --batch and --renderset do
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
    public static int RunFrames(string outDir, bool noSym, bool builtInOnly = false)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var templates = new TemplateService().LoadAll();
            // builtin: the docs showcase must not pick up whatever frames this machine has imported.
            if (builtInOnly)
                templates = templates.Where(t => TemplateService.IsBuiltIn(
                    Path.GetFileName(Path.GetDirectoryName(t.FramePath) ?? "") ?? "")).ToList();
            var symbols = new SymbolService();
            var card = new Cardinator.Models.CardModel
            {
                Name = "Frame Sample", ManaCost = "{2}{R}{W}", TypeLine = "Legendary Creature — Knight",
                RulesText = "First strike, vigilance\n{T}: Draw a card, then discard a card.",
                FlavorText = "One card, every frame.", Power = "3", Toughness = "3",
                SetCode = "CST", CollectorNumber = "1", Rarity = "M", Artist = "Cardinator Demo",
                ArtPath = "examples/02-custom-set/art/aria.png",   // relative to the repo root (regen-docs runs there)
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

    /// <summary>
    /// The full layout audit (<c>--qa &lt;outDir&gt; full</c>): every layout case and stress card on EVERY installed frame,
    /// each checked by <see cref="LayoutAudit"/> for text that's cut off, off the card, on other text or a symbol, under
    /// the P/T box or outside its box. Writes one contact sheet per case (every frame side by side) and
    /// LAYOUT-AUDIT.txt. Returns 0 when nothing was found, 3 when something was.
    /// </summary>
    public static int RunQaFull(string outDir, string? only = null, bool keepCards = false)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var templates = new TemplateService().LoadAll().Where(t => !t.Spec.IsLandscape).ToList();
            if (templates.Count == 0) { Console.Error.WriteLine("QA: no templates."); return 2; }
            var renderer = new CardRenderer(new SymbolService(drawnOnly: true));
            var artPath = Path.Combine(outDir, "_qa_art.png");
            MakeSolidArt(artPath);

            var report = new List<string>();
            int renders = 0, flagged = 0;
            var cases = QaLayoutCards(artPath).Select(c => c.label).Concat(QaStressCards(artPath).Select(c => c.label)).ToList();
            // A case name that matches nothing (a typo) audited nothing — not a clean pass.
            if (only != null && !cases.Any(l => l.Contains(only, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"QA: no case matches “{only}”. The cases are: {string.Join(", ", cases)}.");
                return 2;
            }
            foreach (var label in cases)
            {
                if (only != null && !label.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                var results = new List<QaResult>();
                foreach (var tpl in templates)
                {
                    // A fresh card per frame: rendering sets its frame.
                    var card = QaLayoutCards(artPath).Concat(QaStressCards(artPath)).First(c => c.label == label).card;
                    card.TemplateName = tpl.Name;
                    var resolved = TemplateService.ResolveFor(card, tpl);
                    var drawing = renderer.DrawToGroup(card, tpl);
                    var findings = LayoutAudit.Inspect(drawing, card, resolved.Spec, resolved.FrameImage);
                    var bmp = renderer.RenderToBitmap(card, tpl, supersample: 1);
                    if (keepCards)
                    {
                        var dir = Directory.CreateDirectory(Path.Combine(outDir, "cards", TextUtil.Slug(label))).FullName;
                        CardExporter.Save(bmp, Path.Combine(dir, TextUtil.Slug(tpl.Name) + ".jpg"));
                    }
                    var issues = findings.Select(f => new ValidationIssue(IssueSeverity.Warning, f.Kind, f.ToString())).ToList();
                    results.Add(new QaResult(tpl.Name, tpl.Name, bmp, issues));
                    renders++;
                    if (findings.Count > 0)
                    {
                        flagged++;
                        report.Add($"{label}  [{tpl.Name}]");
                        report.AddRange(findings.Select(f => "    " + f));
                        report.Add("");
                    }
                }
                BuildContactSheet(results, Path.Combine(outDir, "audit-" + TextUtil.Slug(label) + ".png"), label);
            }
            report.Insert(0, $"CARDINATOR LAYOUT AUDIT — {renders} renders, {flagged} with findings");
            report.Insert(1, "");
            File.WriteAllText(Path.Combine(outDir, "LAYOUT-AUDIT.txt"), string.Join("\n", report));
            Console.WriteLine($"Layout audit: {renders} renders, {flagged} with findings. Wrote LAYOUT-AUDIT.txt and the sheets to {outDir}.");
            return flagged > 0 ? 3 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("QA FAILED: " + ex); return 1; }
    }

    /// <summary>Cards that push a layout to its limits: the longest name, cost, type line and rules a card can carry.</summary>
    internal static List<(CardModel card, string tplName, string label)> QaStressCards(string art)
    {
        int n = 500;
        CardModel C(string name, string mana, string type, string rules, string pow = "", string tou = "", string flavor = "", string artPath = "") => new()
        {
            Name = name, ManaCost = mana, TypeLine = type, RulesText = rules, FlavorText = flavor, Power = pow, Toughness = tou,
            ArtPath = artPath, SetCode = "QAS", CollectorNumber = (++n).ToString(), Rarity = "M", Artist = "QA Harness With A Long Name",
        };
        return new()
        {
            (C("Ixhelotl, Unbroken Scion of the Last Eternal Dawn", "{2}{W}{U}", "Legendary Creature — Human Cleric", "Vigilance", "3", "4", artPath: art), "", "Stress: very long name"),
            (C("Prismatic Omen", "{X}{X}{2}{W}{U}{B}{R}{G}", "Sorcery", "Each player draws X cards.", artPath: art), "", "Stress: very long cost"),
            (C("Long Lineage", "{3}{B}", "Legendary Snow Artifact Creature — Phyrexian Praetor Horror Construct", "Menace", "5", "5", artPath: art), "", "Stress: very long type line"),
            (C("Wall of Words", "{3}{U}{U}", "Creature — Sphinx",
                "Flying, ward {2}\nWhenever Wall of Words enters or attacks, draw two cards, then discard a card. If you discarded a land card this way, create a 2/2 blue Drake creature token with flying.\nAt the beginning of your end step, if you've drawn three or more cards this turn, each opponent mills three cards and you gain 3 life.\n{4}{U}: Return target nonland permanent to its owner's hand.",
                "4", "6", "Knowledge outlives the knower.", art), "", "Stress: wall of rules text"),
            (C("Towering Colossus", "{8}", "Artifact Creature — Golem", "Trample", "12", "12", artPath: art), "", "Stress: two-digit P/T"),
            (C("Mirror Mob", "{2}{G}", "Creature — Elemental", "Mirror Mob's power and toughness are each equal to the number of creatures you control.", "*", "*+1", artPath: art), "", "Stress: star P/T"),
            (C("Flavor Only", "{1}{G}", "Creature — Bear", "", "2", "2", "A bear. Just a bear. It has been a bear for a very long time and intends to remain one."), "", "Stress: flavor only, no art"),
        };
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

    internal static List<(CardModel card, string tplName, string label)> QaLayoutCards(string art)
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
            (C("QA Spacecraft", "{2}{U}", "Artifact — Spacecraft", "Station (Tap another creature you control: Put charge counters equal to its power on this Spacecraft. Station only as a sorcery. It's an artifact creature at 12+.)\n3+ | Whenever you cast an artifact spell, draw a card.\n12+ | Flying\nThis Spacecraft gets +1/+0 for each artifact you control.", art, "0", "8"), "Ocean Blue", "Station"),
            (C("QA Case", "{1}{R}{R}", "Enchantment — Case", "When this Case enters, it deals 3 damage to target creature an opponent controls.\nTo solve — Three or more sources you controlled dealt damage this turn. (If unsolved, solve at the beginning of your end step.)\nSolved — Sacrifice this Case: Exile the top three cards of your library. Choose one of them.", art), "Sealed Gate", "Case (medallion frame)"),
            (MeldBack(C("QA Brisela", "", "Legendary Creature — Eldrazi Angel", "Flying, first strike, vigilance, lifelink", art, "9", "10"), "top"), "Gold Multicolor", "Meld back (top half)"),
            (MeldBack(C("QA Brisela", "", "Legendary Creature — Eldrazi Angel", "Flying, first strike, vigilance, lifelink", art, "9", "10"), "bottom"), "Alchemist's Steel", "Meld back (bottom half, picture frame)"),
            (WithAdventure(C("QA Giant", "{2}{R}", "Creature — Giant", "Whenever this creature becomes the target of a spell, it deals 2 damage to that spell's controller.", art, "4", "3")), "Crimson Red", "Adventure (storybook)"),
            (WithAdventure(C("QA Rider", "{1}{B}{B}", "Creature — Zombie Knight", "Lifelink", art, "2", "3")), "Full Art", "Adventure (full art)"),
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
            (HalfFrame(WithSplitHalf(C("QA Tear", "{1}{R}", "Instant", "Destroy target artifact.\nFuse (You may cast one or both halves of this card from your hand.)", art)), "Parchment"), "Crimson Red", "Split card (other half on its own frame, Fuse bar per half)"),
            (HalfFrame(WithAftermath(C("QA Bound", "{1}{B}", "Instant", "Target creature gets +1/+0 until end of turn.", art)), "Arcane Parchment"), "Alchemist's Steel", "Aftermath (lower half on its own picture frame)"),
            (C("QA Soldier", "", "Token Creature — Soldier", "", art, "1", "1"), "Gold Multicolor", "Token (vanilla: no text box)"),
            (C("QA Spirit", "", "Token Creature — Spirit", "Flying\nWhen this token dies, draw a card.", art, "1", "1"), "Crimson Red", "Token (short text box)"),
            (C("QA Emblem", "", "Emblem — Tester", "Creatures you control get +2/+2 and have flying.", art), "Planeswalker", "Emblem"),
            (C("QA Thopter", "", "Token Artifact Creature — Thopter", "", art, "1", "1"), "Full Art", "Token (full art, vanilla)"),
            (C("QA Construct", "", "Token Artifact Creature — Construct", "This token gets +1/+1 for each artifact you control.", art, "0", "0"), "Alchemist's Steel", "Token (picture frame rearranged, medallion)"),
            (C("QA Gold", "", "Token Artifact — Gold", "", art), "Sealed Gate", "Token (picture frame, vanilla: small box)"),
            (WithAftermath(C("QA Destined", "{1}{B}", "Instant", "Target creature gets +1/+0 and gains indestructible until end of turn.", art)), "Midnight", "Aftermath"),
            (WithAftermath(C("QA Fated", "{2}{G}", "Sorcery", "Search your library for a basic land card.", art)), "Sealed Gate", "Aftermath (picture frame's sideways version)"),
        };

        static CardModel MeldBack(CardModel melded, string half)
        {
            // A meld part's back IS the melded card, of which it prints one half.
            var part = new CardModel { Name = "QA Meld Part", MeldHalf = half, DfcStyle = "meld" };
            part.BackFace = melded;
            return part.BackFace;
        }
        static CardModel WithAdventure(CardModel c)
        {
            c.Layout = "adventure";
            c.AdventureName = "QA Stomp"; c.AdventureCost = "{1}{R}"; c.AdventureType = "Instant — Adventure";
            c.AdventureText = "Damage can't be prevented this turn. QA Stomp deals 2 damage to any target.";
            return c;
        }
        static CardModel WithDefense(CardModel c, string defense) { c.Defense = defense; return c; }
        static CardModel HalfFrame(CardModel c, string frame) { c.HalfTemplateName = frame; return c; }
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
