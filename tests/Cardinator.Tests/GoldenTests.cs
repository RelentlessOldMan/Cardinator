using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Golden-image regression tests: render fixed cards and compare against committed baselines, so an
/// unintended change to the renderer/layout is caught. A small tolerance absorbs anti-aliasing noise.
///
/// The cards deliberately use NO mana/inline symbols so the output doesn't depend on which Scryfall
/// SVGs happen to be cached. Baselines live next to this file under <c>golden/</c> and are created on
/// first run. To intentionally re-baseline after a deliberate visual change, delete <c>golden/</c>
/// (or set the env var <c>CARDINATOR_UPDATE_GOLDEN=1</c>) and run the tests again.
/// </summary>
public class GoldenTests
{
    // Max fraction of pixels allowed to differ (per-channel) beyond DiffThreshold.
    private const double MaxDiffFraction = 0.02;
    private const int DiffThreshold = 24;   // per-channel 0-255

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "framed-creature", "Crimson Red", new CardModel
        {
            Name = "Golden Bear", TypeLine = "Creature — Bear",
            RulesText = "Vigilance\nWhenever Golden Bear attacks, it gains trample until end of turn.",
            FlavorText = "Older than the forest it guards.", Power = "4", Toughness = "5",
            SetCode = "GLD", CollectorNumber = "1", Rarity = "R", Artist = "Golden Test",
        }};
        yield return new object[] { "fullart-land", "Full Art", new CardModel
        {
            Name = "Golden Vista", TypeLine = "Land",
            RulesText = "Golden Vista enters the battlefield tapped.\nAdd one mana of any color.",
            FlavorText = "The road remembers every traveler.",
            SetCode = "GLD", CollectorNumber = "2", Rarity = "M", Artist = "Golden Test",
        }};
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Render_MatchesGoldenBaseline(string key, string templateName, CardModel card)
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var template = templates.FirstOrDefault(t => t.Name == templateName) ?? templates[0];

            // Deterministic solid art so compositing is exercised without external files.
            var artPath = SolidPngFile(key.Contains("full") ? Color.FromRgb(40, 70, 120) : Color.FromRgb(70, 110, 60));
            try
            {
                card.ArtPath = artPath;
                card.TemplateName = template.Name;
                var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, template, supersample: 1);
                var actual = ToBgra32(bmp);

                Directory.CreateDirectory(GoldenDir());
                var baseline = Path.Combine(GoldenDir(), key + ".png");

                if (ShouldUpdate() || !File.Exists(baseline))
                {
                    SavePng(bmp, baseline);
                    return;   // bootstrap: baseline created, nothing to compare yet
                }

                var expected = ToBgra32(LoadPng(baseline));
                Assert.Equal(expected.Length, actual.Length);

                double frac = DiffFraction(expected, actual);
                Assert.True(frac <= MaxDiffFraction,
                    $"'{key}' differs from golden baseline by {frac:P2} of pixels (limit {MaxDiffFraction:P0}). " +
                    $"If this change is intended, delete tests/Cardinator.Tests/golden/{key}.png and re-run.");
            }
            finally { File.Delete(artPath); }
        });

    // --- helpers ------------------------------------------------------------

    private static bool ShouldUpdate()
        => Environment.GetEnvironmentVariable("CARDINATOR_UPDATE_GOLDEN") == "1";

    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static double DiffFraction(byte[] a, byte[] b)
    {
        int different = 0, pixels = a.Length / 4;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > DiffThreshold ||
                Math.Abs(a[i + 1] - b[i + 1]) > DiffThreshold ||
                Math.Abs(a[i + 2] - b[i + 2]) > DiffThreshold ||
                Math.Abs(a[i + 3] - b[i + 3]) > DiffThreshold)
                different++;
        }
        return pixels == 0 ? 1 : (double)different / pixels;
    }

    private static byte[] ToBgra32(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[conv.PixelHeight * stride];
        conv.CopyPixels(px, stride, 0);
        return px;
    }

    private static BitmapSource LoadPng(string path)
    {
        using var fs = File.OpenRead(path);
        return BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string SolidPngFile(Color c)
    {
        const int w = 900, h = 700, stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var path = Path.Combine(Path.GetTempPath(), $"golden-art-{Guid.NewGuid():N}.png");
        SavePng(bmp, path);
        return path;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
