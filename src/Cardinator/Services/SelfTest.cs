using System.IO;

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

            var models = cards.Select(c => c.Card).ToList();
            if (report.ExtraBackFaces.Count > 0)
            {
                models.AddRange(report.ExtraBackFaces);
                Console.WriteLine($"Added {report.ExtraBackFaces.Count} double-faced back(s).");
            }

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

            var models = cards.Select(c => c.Card).ToList();
            models.AddRange(report.ExtraBackFaces);
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

    /// <summary>Reads an "name=value" integer option from the extra CLI args.</summary>
    private static int OptInt(string[] opts, string name, int fallback)
    {
        var hit = opts.FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return hit != null && int.TryParse(hit[(name.Length + 1)..], out var v) ? v : fallback;
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

    private static string Slug(string s) => TextUtil.Slug(s);
}
