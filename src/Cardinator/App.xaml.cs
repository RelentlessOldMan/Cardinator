using System.IO;
using System.Windows;
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
            var maxArg = rest.FirstOrDefault(a => a.StartsWith("max=", StringComparison.OrdinalIgnoreCase));
            if (maxArg != null) int.TryParse(maxArg[4..], out max);
            else { var bare = rest.FirstOrDefault(a => int.TryParse(a, out _)); if (bare != null) int.TryParse(bare, out max); }
            int code = SelfTest.RunSearch(e.Args[1], e.Args[2], sheet, a4, Math.Clamp(max <= 0 ? 60 : max, 1, 500));
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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
