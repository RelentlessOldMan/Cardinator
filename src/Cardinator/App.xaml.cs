using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// Application entry point. Normally shows the main window; with "--selftest [outDir]"
/// it renders the sample cards to PNG headlessly (no window) and exits — used to verify
/// the render pipeline without popping a UI.
/// </summary>
public partial class App : Application
{
    internal const string Version = "Cardinator 1.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] is "--help" or "-h" or "/?")
        {
            PrintUsage();
            Shutdown(0);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] is "--version" or "-v")
        {
            Console.WriteLine(Version);
            Shutdown(0);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            var outDir = e.Args.Length > 1 ? e.Args[1] : AppPaths.OutputDir;
            int code = SelfTest.Run(outDir);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--lookup")
        {
            var outDir = e.Args.Length > 2 ? e.Args[2] : AppPaths.OutputDir;
            int code = SelfTest.RunLookup(e.Args[1], outDir);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--frames")
        {
            int code = SelfTest.RunFrames(e.Args[1], e.Args.Skip(2).Contains("nosym"));
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--cardback")
        {
            int code = SelfTest.RunCardBack(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--newtemplate")
        {
            int code = SelfTest.RunNewTemplate(e.Args[1], e.Args[2], e.Args.Skip(3).Contains("fullart"));
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--render")
        {
            int code = SelfTest.RunRender(e.Args[1], e.Args[2], e.Args.Skip(3).ToArray());
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--permute")
        {
            int code = SelfTest.RunPermute(e.Args[1], e.Args[2]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--batch")
        {
            var artDir = e.Args.Length > 3 ? e.Args[3] : null;
            int code = SelfTest.RunBatch(e.Args[1], e.Args[2], artDir);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--sheet")
        {
            var rest = e.Args.Skip(3).ToArray();
            bool a4 = rest.Any(a => a.Equals("a4", StringComparison.OrdinalIgnoreCase));
            var artDir = rest.FirstOrDefault(a => !a.Equals("a4", StringComparison.OrdinalIgnoreCase)
                                                  && !a.Equals("letter", StringComparison.OrdinalIgnoreCase));
            int code = SelfTest.RunSheet(e.Args[1], e.Args[2], artDir, a4);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--search")
        {
            var rest = e.Args.Skip(3).ToArray();
            bool sheet = rest.Any(a => a.Equals("sheet", StringComparison.OrdinalIgnoreCase));
            bool a4 = rest.Any(a => a.Equals("a4", StringComparison.OrdinalIgnoreCase));
            int max = 60;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Integer;
            var maxArg = rest.FirstOrDefault(a => a.StartsWith("max=", StringComparison.OrdinalIgnoreCase));
            if (maxArg != null) int.TryParse(maxArg[4..], ns, inv, out max);
            else { var bare = rest.FirstOrDefault(a => int.TryParse(a, ns, inv, out _)); if (bare != null) int.TryParse(bare, ns, inv, out max); }
            int code = SelfTest.RunSearch(e.Args[1], e.Args[2], sheet, a4, Math.Clamp(max <= 0 ? 60 : max, 1, 500));
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--qa")
        {
            int code = SelfTest.RunQa(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--docs")
        {
            int code = RunDocs(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--uishot")
        {
            int code = RunUiShot(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 3 && e.Args[0] == "--appshot")
        {
            // --appshot <csv> <artDir> <out.png> [selectNameSubstring]
            int code = RunAppShot(e.Args[1], e.Args[2], e.Args[3], e.Args.Length > 4 ? e.Args[4] : null);
            Shutdown(code);
            return;
        }

        // Safety net for the interactive app: a stray exception on the UI thread (e.g. from an event
        // handler after an await) shows a message and keeps the app alive instead of hard-crashing and
        // losing the user's unsaved work. Headless CLI paths above return before this is wired.
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("Something went wrong:\n\n" + ex.Exception.Message,
                "Cardinator", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "crash.log"),
                $"{DateTime.Now:o} {ex.ExceptionObject}\n"); } catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => ex.SetObserved();

        // Dark title bars + app icon on every window (matches the app's dark theme).
        ThemeHelper.ApplyToAllWindows();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Renders the app's actual windows to PNGs off-screen (no display needed — works even on a
    /// locked machine), for documentation screenshots. Uses sample data so the shots are populated.
    /// </summary>
    private int RunUiShot(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            WriteWindowShots(outDir);
            Console.WriteLine($"UI screenshots written to {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UI screenshot FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>Renders the app's four windows (main, details, frame design, help) to PNGs in a folder.
    /// Shared by --uishot and --docs so the documentation screenshots always match the real UI.</summary>
    private void WriteWindowShots(string outDir)
    {
        var main = new MainWindow { SuppressClosePrompt = true };
        // The default starter card already ships with bundled art, so the flagship shot isn't blank.
        SaveWindow(main, 1240, 820, Path.Combine(outDir, "app-main.png"));

        var sample = SampleCards.All("Ocean Blue").First().Clone();
        var details = new DetailsWindow(sample);
        SaveWindow(details, 560, 760, Path.Combine(outDir, "app-details.png"));

        var help = new HelpWindow(Version);
        SaveWindow(help, 600, 720, Path.Combine(outDir, "app-help.png"));

        var tpls = new TemplateService().LoadAll();
        var designTpl = tpls.FirstOrDefault(t => t.Name == "Azure Modern") ?? tpls.First();
        var design = new FrameDesignWindow(designTpl);
        SaveWindow(design, 940, 760, Path.Combine(outDir, "app-frame-design.png"));

        var bulk = new BulkEditWindow(tpls.Select(t => t.Name));
        SaveWindow(bulk, 430, 470, Path.Combine(outDir, "app-bulk-edit.png"));

        var deck = new DeckListWindow();
        SaveWindow(deck, 560, 560, Path.Combine(outDir, "app-deck-list.png"));

        var confirm = new ConfirmDialog("Unsaved changes",
            "You have unsaved changes. Save them before continuing?", "Save", "Don't save", "Cancel");
        SaveWindow(confirm, 460, 150, Path.Combine(outDir, "app-confirm.png"));

        var search = new ScryfallSearchWindow(new ScryfallClient());
        foreach (var c in SampleCards.All("Gold Multicolor"))
            search.Results.Add(new ScryfallSearchWindow.ResultItem(c) { IsChecked = true });
        SaveWindow(search, 560, 680, Path.Combine(outDir, "app-search.png"));
    }

    /// <summary>
    /// One-command documentation build: regenerates every screenshot the guide references (the four app
    /// windows + a few hero card renders + a frames overview) and writes an illustrated QUICKSTART.md that
    /// embeds them. Run this after any UI/frame change so the docs never drift from the app.
    /// </summary>
    private int RunDocs(string outDir)
    {
        try
        {
            var images = Path.Combine(outDir, "images");
            Directory.CreateDirectory(images);
            WriteWindowShots(images);

            // Hero card renders (one striking card per a few signature frames).
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            System.Threading.Tasks.Task.Run(() => symbols.PrimeAsync()).GetAwaiter().GetResult();
            var renderer = new CardRenderer(symbols);
            var heroArt = "examples/01-real-cards-custom-art/art/swiftspear.png";   // relative to the repo root
            foreach (var (tpl, file) in new[] { ("Gold Multicolor", "hero-gold.png"), ("Azure Modern", "hero-modern.png"), ("Full Art", "hero-fullart.png") })
            {
                var t = templates.FirstOrDefault(x => x.Name == tpl) ?? templates[0];
                var card = SampleCards.All(t.Name).First().Clone();
                if (File.Exists(heroArt)) card.ArtPath = heroArt;
                var bmp = renderer.RenderToBitmap(card, t, supersample: 2);
                CardExporter.SavePng(bmp, Path.Combine(images, file));
            }

            File.WriteAllText(Path.Combine(outDir, "QUICKSTART.md"), QuickstartMarkdown());
            Console.WriteLine($"Docs written to {outDir} (QUICKSTART.md + images/).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Docs build FAILED: " + ex);
            return 1;
        }
    }

    private static string QuickstartMarkdown() => $$"""
        # Cardinator — Quick Start

        *This guide is generated automatically by `Cardinator.exe --docs`; the screenshots are the real app.*

        Cardinator makes custom Magic-style cards from your own art — one at a time, or hundreds at once.

        ![The Cardinator editor](images/app-main.png)

        ## Make one card

        1. **Name it.** Type a name in the CARD NAME box. To reuse a real card's stats, type its name and
           click **Search** — Cardinator fills the mana cost, type, rules text, power/toughness and even
           downloads the art from Scryfall. (You can override anything afterwards.)
        2. **Pick a frame.** Choose one from the FRAME dropdown. Click **Design…** to mix and match the
           frame's look (below).
        3. **Add your art.** Click **Change art…**, **Paste** a copied image or URL, or just drag an image
           onto the window. Drag on the preview to pan, scroll to zoom.
        4. **Edit the details.** Click **Edit details…** for mana cost, type line, rules/flavor text,
           power/toughness, loyalty, set, collector number, artist and more. Type mana as `{2}{U}{U}` —
           it renders as symbols, in the title and inside rules text.
        5. **Watch the CHECKS panel.** It flags problems as you go — missing art, an unrecognized symbol, a
           footer overlapping the text box, duplicate collector numbers — so nothing surprises you at export.
        6. **Export.** Click **Export PNG…** for a print-quality image, or **Copy image** to paste it
           straight into a chat.

        ### Editing the details
        ![Card details editor](images/app-details.png)

        ### Designing the frame
        Mix and match the frame's style, connected panels, textured background, bottom taper, top emblem,
        royal sub-border, border thickness and colors — all live.

        ![Frame design](images/app-frame-design.png)

        ## Frames at a glance
        | Gold Multicolor | Azure Modern | Full Art |
        |---|---|---|
        | ![](images/hero-gold.png) | ![](images/hero-modern.png) | ![](images/hero-fullart.png) |

        ## Make a whole set at once

        1. Put your card names in a text or CSV file — one per line, or with columns for art and frame:
           ```
           Lightning Bolt, bolt.png, Crimson Red
           Counterspell,   counter.png, Ocean Blue
           ```
        2. Click **Import list / CSV…** (or drop the file on the window). Cardinator fills any blank fields
           from Scryfall and matches art files by name.
        3. Click **Look up missing** if you left fields blank, and **Match art folder…** to attach a folder
           of images by filename.
        4. **Export all…** writes every card to a PNG, or **Print sheet…** lays them out at real card size
           on Letter/A4 pages (with cut marks and optional bleed) ready to print.

        ## Keyboard shortcuts
        `Ctrl+S` save · `Ctrl+O` open · `Ctrl+N` new · `Ctrl+L` look up · `Ctrl+E` export · `Ctrl+D` duplicate · `F1` help

        ---
        *Cardinator is for personal/fan use. Card frames and symbols are original; it isn't affiliated with
        Wizards of the Coast.*
        """;

    /// <summary>
    /// Renders the main window with a real deck loaded from a CSV (Scryfall-filled) — a "here's the
    /// app doing the thing" screenshot for the docs. Off-screen, so it works on a locked machine.
    /// </summary>
    private int RunAppShot(string csvPath, string artDir, string outPng, string? selectName)
    {
        try
        {
            var content = File.ReadAllText(csvPath);
            var templates = new TemplateService().LoadAll();
            var imported = ImportService.Parse(content, artDir, templates[0].Name);
            System.Threading.Tasks.Task.Run(() => BatchService.FillFromScryfallAsync(imported, downloadArt: false))
                .GetAwaiter().GetResult();

            var main = new MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            foreach (var it in imported) main.Cards.Add(it.Card);
            main.SelectedCard = main.Cards.FirstOrDefault(c =>
                selectName != null && c.Name.Contains(selectName, StringComparison.OrdinalIgnoreCase))
                ?? main.Cards.FirstOrDefault();

            SaveWindow(main, 1240, 820, outPng);
            Console.WriteLine($"App screenshot written to {outPng} ({main.Cards.Count} cards).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("App screenshot FAILED: " + ex);
            return 1;
        }
    }

    private static void SaveWindow(Window w, int width, int height, string path)
    {
        if (w.Content is not FrameworkElement root) return;

        // Give the root the window's background so transparent areas aren't see-through.
        if (root is Panel p && p.Background == null)
            p.Background = (Brush)Current.Resources["Bg"];

        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        // Let bindings/layout settle (e.g. the live preview image) before rasterizing.
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        CardExporter.SavePng(rtb, path);
        Console.WriteLine($"Wrote {path} ({width}x{height}).");
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            {Version} - custom Magic-style card maker

            Run with no arguments to open the app. It creates a "CardinatorData" folder
            next to the exe for templates, mana symbols, saved cards and exported PNGs.

            Headless modes (no window):
              Cardinator.exe --selftest <outDir>
                  Render the built-in sample cards to PNGs.
              Cardinator.exe --lookup "<card name>" <outDir>
                  Fetch one card from Scryfall and render it.
              Cardinator.exe --render <card.json> <out.png|.jpg> [scale=N] [dpi=N] [bleed=N] [q=N]
                  Render a saved card JSON. scale=supersample, dpi=stamp, bleed=print margin px,
                  q=JPEG quality. .jpg output writes a JPEG.
              Cardinator.exe --newtemplate "<name>" <frame.png | url> [fullart]
                  Create a custom template from your own frame image (local file or URL).
              Cardinator.exe --cardback <out.png> ["Wordmark"]
                  Render the decorative card back (for double-sided printing).
              Cardinator.exe --frames <outDir> [nosym]
                  Render one sample card on every installed frame (a style showcase).
              Cardinator.exe --permute <card.json> <outDir>
                  Render one card across 32 composable-frame knob combinations.
              Cardinator.exe --uishot <outDir>
                  Render the app's own windows to PNGs off-screen (documentation screenshots).
              Cardinator.exe --qa <outDir>
                  Render every layout across the frames, run automated checks, and write
                  contact sheets (qa-layouts.png, qa-frames.png) + QA-REPORT.txt.
              Cardinator.exe --docs <outDir>
                  Regenerate all documentation screenshots + an illustrated QUICKSTART.md.
              Cardinator.exe --appshot <list.csv> <artDir> <out.png> ["name"]
                  Render the main window with a deck loaded (documentation screenshots).
              Cardinator.exe --batch <list.csv> <outDir> [artDir]
                  Import a name list / CSV, fill blanks from Scryfall, render all.
              Cardinator.exe --sheet <list.csv> <outDir> [artDir] [a4]
                  Same as --batch, but compose printable 3x3 sheet pages.
              Cardinator.exe --search "<query>" <outDir> [sheet] [a4] [max=N]
                  Import a Scryfall search (e.g. "t:dragon", "set:dom") with art and render it.

            Other:
              --help, -h        Show this help.
              --version, -v     Show the version.
            """);
    }
}
