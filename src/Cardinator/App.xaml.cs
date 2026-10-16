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
    /// <summary>App name + version shown in the header, title bar and Help. Derived from the assembly
    /// version (set by &lt;Version&gt; in Cardinator.csproj) so there is ONE source of truth that can never
    /// drift from the real build — critical for correct bug reports.</summary>
    internal static readonly string Version = "Cardinator " + VersionNumber();

    /// <summary>The set commands and how many arguments each needs, counting itself.</summary>
    private static readonly Dictionary<string, int> SetCommandArgs = new()
    { ["--moxfield"] = 3, ["--import"] = 3, ["--renderset"] = 3, ["--restyle"] = 2, ["--info"] = 2 };

    /// <summary>The three-part build number (e.g. "1.1.4") read from the assembly.</summary>
    internal static string VersionNumber()
    {
        var v = typeof(App).Assembly.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Command-line output goes to a pipe or file (a GUI exe has no console of its own): write it as UTF-8 with
        // plain \n line ends, so a script reads accented card and set names (Lim-Dûl, Æther) and bash's `read`
        // doesn't keep a \r on the last field. It was the ANSI code page and CRLF.
        if (Console.IsOutputRedirected)
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
        if (Console.IsErrorRedirected)
            Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

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

        if (CliArgs.Problem(e.Args) is { } problem)
        {
            Console.Error.WriteLine(problem);
            Shutdown(2);
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
            int code = SelfTest.RunFrames(e.Args[1], e.Args.Skip(2).Contains("nosym"), e.Args.Skip(2).Contains("builtin"));
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

        if (e.Args.Length > 2 && e.Args[0] == "--rendercards")
        {
            int code = SelfTest.RunRenderCards(e.Args[1], e.Args[2], e.Args.Skip(3).ToArray());
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

        // Scripting a set: fetch a deck, import it into a set, render, restyle, describe (SetCommands).
        // Too few arguments for one of these is a usage error (exit 2), never the window: a script would hang on it.
        if (e.Args.Length > 0 && SetCommandArgs.TryGetValue(e.Args[0], out var needed) && e.Args.Length < needed)
        {
            Console.Error.WriteLine($"{e.Args[0]}: missing arguments — see docs/CLI.md.");
            Shutdown(2);
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--moxfield") { Shutdown(SetCommands.RunMoxfield(e.Args[1], e.Args[2], e.Args[3..])); return; }
        if (e.Args.Length > 2 && e.Args[0] == "--import") { Shutdown(SetCommands.RunImport(e.Args[1], e.Args[2], e.Args[3..])); return; }
        if (e.Args.Length > 2 && e.Args[0] == "--renderset") { Shutdown(SetCommands.RunRenderSet(e.Args[1], e.Args[2], e.Args[3..])); return; }
        if (e.Args.Length > 1 && e.Args[0] == "--restyle") { Shutdown(SetCommands.RunRestyle(e.Args[1], e.Args[2..])); return; }
        if (e.Args.Length > 1 && e.Args[0] == "--info") { Shutdown(SetCommands.RunInfo(e.Args[1], e.Args[2..])); return; }
        if (e.Args.Length > 0 && e.Args[0] == "--listframes") { Shutdown(SetCommands.RunListFrames(e.Args[1..])); return; }

        if (e.Args.Length > 1 && e.Args[0] == "--qa")
        {
            int code = e.Args.Length > 2 && e.Args[2] == "full"
                ? SelfTest.RunQaFull(e.Args[1], e.Args.Skip(3).FirstOrDefault(x => x != "cards"), e.Args.Contains("cards"))
                : SelfTest.RunQa(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--docs")
        {
            int code = RunDocs(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 2 && e.Args[0] == "--uishot" && e.Args[2] == "layouts")
        {
            int code = RunLayoutShots(e.Args[1]);
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

        // Anything else on the command line (a mode given too few arguments, a misspelled one, one typed without its
        // dashes — "info x.cardinator" — or with a pasted dash, "—info") is a usage error (exit 2), never the window:
        // the window opens no files from the command line, and a script waiting on it would hang.
        if (e.Args.Length > 0 && !(e.Args[0] == "--editframe" && e.Args.Length > 1))
        {
            Console.Error.WriteLine($"{e.Args[0]}: unknown option, or missing arguments - run Cardinator --help, or see docs/CLI.md.");
            Shutdown(2);
            return;
        }

        // Safety net for the interactive app: a stray exception on the UI thread (e.g. from an event
        // handler after an await) shows a message and keeps the app alive instead of hard-crashing and
        // losing the user's unsaved work. Headless CLI paths above return before this is wired.
        // Windows logging off / shutting down / restarting for an update never raises Window.Closing, so the
        // save prompt can't run: keep a recovery copy of any unsaved work instead (offered on the next launch).
        SessionEnding += (_, _) => (MainWindow as MainWindow)?.WriteRecovery();
        DispatcherUnhandledException += (_, ex) =>
        {
            try { (MainWindow as MainWindow)?.WriteRecovery(); } catch { /* keep going */ }
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

        // Open straight into the frame layout editor on one template folder (no main window).
        if (e.Args.Length > 1 && e.Args[0] == "--editframe")
        {
            var tpl = LoadTemplateDir(e.Args[1]);
            if (tpl == null)
            {
                var why = $"Couldn't load a template from:\n{e.Args[1]}\n\nExpected a folder with template.json + frame.png.";
                // A script (its error output captured) gets the message there, not a box nobody will click.
                if (Console.IsErrorRedirected) Console.Error.WriteLine("--editframe: " + why.Replace("\n\n", " ").Replace("\n", " "));
                else MessageBox.Show(why, "Cardinator", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            // Optional: --editframe <templateDir> [card.json] previews the real card in the editor.
            CardModel? previewCard = null;
            if (e.Args.Length > 2 && File.Exists(e.Args[2]))
                try { previewCard = CardModel.Load(e.Args[2]); } catch { /* fall back to the sample card */ }

            var editor = new FrameDesignWindow(tpl, previewCard)
            {
                Standalone = true,
                Title = "Frame layout — " + tpl.Name,
                // No owner window in standalone mode, so make it a normal, findable top-level window.
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            MainWindow = editor;
            editor.Show();
            editor.Activate();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.OfferRecovery();
    }

    /// <summary>Loads a single on-disk template folder (template.json + frame.png) for --editframe.</summary>
    private static Template? LoadTemplateDir(string dir)
    {
        try
        {
            var specPath = Path.Combine(dir, "template.json");
            var framePath = Path.Combine(dir, "frame.png");
            if (!File.Exists(specPath) || !File.Exists(framePath)) return null;
            var spec = TemplateSpec.Load(specPath);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(Path.GetFullPath(framePath));
            bi.EndInit();
            bi.Freeze();
            return new Template { Name = spec.Name, Spec = spec, FramePath = framePath, FrameImage = bi };
        }
        catch { return null; }
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

    /// <summary>The main window in each card layout and side (front, back face, the set's card back, a split card's
    /// other half, a meld card's whole melded card), and the Set settings window — for reviewing the layout UI.</summary>
    private int RunLayoutShots(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            void Shot(string name, Action<MainWindow, CardModel> setUp)
            {
                var main = new MainWindow { SuppressClosePrompt = true };
                var card = main.Cards[0];
                setUp(main, card);
                SaveWindow(main, 1240, 820, Path.Combine(outDir, name + ".png"));

            }
            Shot("1-normal-front", (_, _) => { });
            Shot("2-normal-cardback", (m, _) => m.ShowSide(back: true));
            Shot("3-dfc-front", (m, c) => m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.DoubleFaced));
            Shot("4-dfc-back", (m, c) =>
            {
                m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.DoubleFaced);
                c.BackFace!.Name = "Seraph Ascendant";
                c.BackFace.TypeLine = "Legendary Creature — Angel";
                c.BackFace.RulesText = "Flying\nWhenever another creature you control dies, draw a card.";
                m.ShowSide(back: true);
            });
            Shot("5-meld-back-whole", (m, c) =>
            {
                m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.Meld);
                c.BackFace!.Name = "The Last Light Unbound";
                m.ShowSide(back: true);
                m.WholeMeldBox.IsChecked = true;
            });
            Shot("6-flip-other", (m, c) =>
            {
                m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.Flip);
                c.OtherHalf!.Name = "Seraph Fallen";
                m.ShowHalf(other: true);
            });
            Shot("7-split-right", (m, c) =>
            {
                m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.Split);
                c.OtherHalf!.Name = "Dusk";
                c.OtherHalf.TypeLine = "Instant";
                c.OtherHalf.RulesText = "Destroy target creature.";
                m.ShowHalf(other: true);
            });
            Shot("8-adventure", (m, c) => m.ChangeLayout(c, Cardinator.MainWindow.CardLayout.Adventure));
            // Laptop screens, maximized: what's left of 1366x768 and 1280x720 under the taskbar and title bar. Every
            // button must still show.
            foreach (var (screen, w, h) in new[] { ("1366x768", 1366, 697), ("1280x720", 1280, 649) })
            {
                var small = new MainWindow { SuppressClosePrompt = true };
                SaveWindow(small, w, h, Path.Combine(outDir, $"8b-laptop-{screen}.png"));
            }
            var empty = new MainWindow { SuppressClosePrompt = true };
            empty.Cards.Clear();
            SaveWindow(empty, 1240, 820, Path.Combine(outDir, "8c-empty-set.png"));

            var tpls = new TemplateService().LoadAll();
            var settings = new SetSettingsWindow(new SetProfile
            {
                SetCode = "CDN", Rarity = "R", Artist = "Sam",
                CardBack = new CardBackDesign { Style = CardBackDesign.DesignedStyle, Color = "#5A1A2A", Accent = "#E0C080" },
            }, "", tpls.Select(t => t.Name), "My Set");
            settings.ModeReplace.IsChecked = true;
            SaveWindow(settings, 760, 700, Path.Combine(outDir, "9-set-settings.png"));
            var deck = new DeckListWindow();
            SaveWindow(deck, 560, 600, Path.Combine(outDir, "10-import-cards.png"));
            Console.WriteLine($"Layout screenshots written to {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Layout screenshots FAILED: " + ex);
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

        var setSettings = new SetSettingsWindow(new SetProfile { SetCode = "CDN", Artist = "Sam" }, "", tpls.Select(t => t.Name), "My Set");
        SaveWindow(setSettings, 760, 640, Path.Combine(outDir, "app-set-settings.png"));

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

        1. **Name it.** Type a name in the CARD NAME box. Making a real card? Type its name and click
           **Look up** (or press **Ctrl+L**): it becomes that card — its cost, type, rules text,
           power/toughness, printing and, unless you've given it your own, its art — straight from Scryfall.
           Your frame always stays. **Undo** puts back what was there.
        2. **Write it.** TYPE LINE, MANA COST and RULES TEXT are right under the name. Type mana as `{2}{U}{U}`
           — it renders as symbols, in the title and inside rules text.
        3. **Pick a frame.** Choose one from the FRAME dropdown (its own row), with **Design…**
           and a **⋯** menu (*Import a frame…*, *Export this frame…*, *Delete this frame…*) beside it. Click **Design…** to mix and match the
           frame's look (below). Different cards in a set can use different frames.
        4. **Add your art.** Click **Change art…**, **Paste** a copied image or URL, or just drag an image
           onto the window. Paste even handles images copied from a web browser. Drag the preview to pan,
           scroll to zoom (hold **Ctrl** for fine zoom); click the preview then use the **arrow keys** to
           nudge the art a pixel at a time, **+ / -** to zoom and **0** to reset. **Reset** (next to Clear)
           puts the art back centred at its original size.
        5. **Edit the details.** Click **Edit details…** for everything else: power/toughness, loyalty,
           flavor text, rarity, set, collector number, artist and more.
        6. **Borrow real stats (optional).** Inside **Edit details…**, use **Fill from Scryfall**: type any
           real card name and it fills the mana cost, type, rules text, power/toughness and printing info —
           while leaving your card's **name, art and frame untouched**. So you can call a card "Boogie
           Woogie" and still pull Lightning Bolt's stats.
        7. **Watch the CHECKS panel** (under the editing column). It flags problems as you go — an
           unrecognized symbol, a footer overlapping the text box, text too long for its box, duplicate
           collector numbers — and notes things like missing art, so nothing surprises you at export.
        8. **Export.** Under the preview, click **Export PNG…** for a print-quality image, or **Copy image**
           to paste it straight into a chat. **Open output folder** opens wherever you last exported.

        ### Editing the details (with Scryfall fill)
        ![Card details editor](images/app-details.png)

        ### Designing the frame
        Mix and match the frame's style, connected panels, textured background, bottom taper, top emblem,
        royal sub-border, border thickness and colors — all live. **Apply** saves your changes; **Save as
        new…** keeps the original untouched and creates a fresh, editable frame. Built-in frames are
        protected — editing one offers to save a copy instead, and *Delete this frame…* only removes your own frames.

        ![Frame design](images/app-frame-design.png)

        ### Importing a frame
        Click **⋯** → *Import a frame…* to add a frame: pick a **PNG**, a **.cardframe**, or a **.zip** bundle from your
        PC with the **Choose file…** button, or paste a web link. (A `.cardframe`/`.zip` can also just be dropped
        onto the window.) A `.cardframe`/`.zip` bundle keeps its tuned regions; a bare image gets default regions
        you can tune. Imported frames are copied into the app's data folder, so the original file can move or be
        deleted afterwards.

        ## Frames at a glance
        | Gold Multicolor | Azure Modern | Full Art |
        |---|---|---|
        | ![](images/hero-gold.png) | ![](images/hero-modern.png) | ![](images/hero-fullart.png) |

        ## Make a whole set at once

        1. Put your card names in a text or CSV file — one per line, or as a CSV whose first row names the
           columns (art, frame, `qty` for several copies, and more — see the
           [CSV columns](USER_GUIDE.md#csv-columns)):
           ```
           name, art, frame
           Lightning Bolt, bolt.png, Crimson Red
           Counterspell,   counter.png, Ocean Blue
           ```
        2. Click **Import cards…** → **Open a file…** (or drop the file on the window). Cardinator fills any
           blank fields from Scryfall and matches art files by name.
        3. Click **Fill in blanks** if you left fields blank (it looks them up on Scryfall), and **Art from
           folder…** to attach a folder of images by filename.
        4. Use **Set settings…** to give every card one frame (and/or set code, artist, rarity, copyright) —
           pick *…and replace them on every card already here*; only the fields you change go on the cards, so you
           can re-frame a whole set in one step. Choose the set's **card back** there too.
        5. **All as images…** writes every card to a PNG, or **Print sheets…** lays them out at real card size
           on Letter pages with cut marks, ready to print (A4 pages and print bleed are on the
           [command line](CLI.md)).

        ## Saving a set
        **Save** writes your project into a **set folder**: the project file plus an `art/` subfolder (your
        images are copied in) and an `out/` subfolder (where exports and sheets default). The first Save
        offers *Documents\Cardinator Sets*, and a set saved into a folder that already holds other things
        gets a folder of its own, named after it. **Save as…** (Ctrl+Shift+S) saves a copy under another
        name or somewhere else. The whole folder is
        self-contained — move, zip or share it and every card still finds its art. One-off single cards need
        no setup: the folder is only created when you first save.

        ## Keyboard shortcuts
        `Ctrl+S` save · `Ctrl+Shift+S` save as · `Ctrl+O` open · `Ctrl+N` new · `Ctrl+L` look up · `Ctrl+E` export · `Ctrl+D` duplicate · `F1` help
        · click the preview, then **arrows** nudge art (Shift = 10px) , **+/-** zoom and **0** resets

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
            var content = ImportService.ReadListFile(csvPath);
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

            GUI:
              Cardinator.exe --editframe <templateDir> [card.json]
                  Open just the frame layout editor on one template folder (template.json +
                  frame.png): drag/resize the text regions, tweak fonts, save in place.
                  Pass a card.json to preview your real card in the editor.

            Headless modes (no window):
              Cardinator.exe --selftest <outDir>
                  Render the built-in sample cards to PNGs.
              Cardinator.exe --lookup "<card name>" <outDir>
                  Fetch one card from Scryfall and render it.
              Cardinator.exe --render <card.json> <out.png|.jpg> [scale=N] [dpi=N] [bleed=N] [q=N] [nosym]
                  Render a saved card JSON. scale=supersample, dpi=stamp, bleed=print margin px,
                  q=JPEG quality, nosym=the app's own pips. .jpg output writes a JPEG.
              Cardinator.exe --rendercards <cards-dir|card.json> <outDir> [templates=<dir>] [scale=N] [nosym]
                  Batch-render a folder of card JSONs to PNGs. templates=<dir> also loads custom
                  frames straight from a folder (no install needed). Each card -> <outDir>/<slug>.png.
              Cardinator.exe --newtemplate "<name>" <frame.png | url> [fullart]
                  Create a custom template from your own frame image (local file or URL).
              Cardinator.exe --cardback <out.png> ["Wordmark"]
                  Render the default card back (for double-sided printing); a set's own is made in Set settings.
              Cardinator.exe --frames <outDir> [nosym] [builtin]
                  Render one sample card on every installed frame (a style showcase).
                  builtin=only the frames that ship with the app.
              Cardinator.exe --permute <card.json> <outDir>
                  Render one card across 32 composable-frame knob combinations.
              Cardinator.exe --uishot <outDir> [layouts]
                  Render the app's own windows to PNGs off-screen (documentation screenshots).
                  layouts=the main window in each card layout and side, instead.
              Cardinator.exe --qa <outDir> [full [<case>] [cards]]
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

            Working with sets (scripting what the window does; exit code 0 = done):
              Cardinator.exe --moxfield <deck link|id> <out.txt|out.json> [format=magicdecks]
                  Fetch a Moxfield deck: .json = the deck's JSON, otherwise a plain decklist.
              Cardinator.exe --import <deck link|list file> <set.cardinator> [frame=<name>] [art=<dir>] [noart] [append] [name=<set name>] [templates=<dir>]
                  Make a set from a Moxfield deck or a list/CSV, filled from Scryfall (with art unless
                  noart), as the window's import does. frame= = the set's frame (cards a CSV gives
                  one keep theirs); art=<dir> = your pictures, matched by file name.
                  An existing set is never touched unless you pass append (adds new names only).
              Cardinator.exe --renderset <set.cardinator> <outDir> [sheet] [a4] [only=<name|name>] [templates=<dir>] [nosym]
                  Render a saved set, as Export does (or as 3x3 print sheets). only= picks cards by name;
                  templates=<dir> also uses frames from a folder without installing them.
              Cardinator.exe --restyle <set.cardinator> [frame=<name>] [art=<dir>] [titles=<file>] [setcode=<code>] [number=<n>] [symbol=<image>] [only=<name|name>] [templates=<dir>]
                  Change cards' frame, art and/or titles in a saved set (the old file is backed up first).
                  titles=<file>: "card name<TAB>new title" lines; the real name becomes the subtitle
                  (exit 3, nothing saved, when it retitles no card). For the whole set, setcode= and
                  symbol= also go into the set's settings.
              Cardinator.exe --info <set.cardinator> [templates=<dir>]
                  List a set's cards with their frame and art status (tab-separated).
              Cardinator.exe --listframes [templates=<dir>]
                  List the installed frame names (what frame= takes).

            Other:
              --help, -h        Show this help.
              --version, -v     Show the version.
            """);
    }
}
