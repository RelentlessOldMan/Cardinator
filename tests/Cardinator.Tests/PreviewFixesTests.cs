using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.33: built-in frames copied by an older version never brought up to date (a P/T notch under the P/T box, the
/// footer on the border); dragging or zooming the art redrawing the whole card on every mouse move; Remove meld leaving
/// a back face behind, which made the card double-faced and greyed out Make split card. In the STA-window collection
/// because some build the main window or answer prompts.
/// </summary>
[Collection("STAWindows")]
public class PreviewFixesTests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    // --- built-in frames from an older version ---------------------------------------------------------

    /// <summary>A built-in as an older version left it: its regions somewhere else, and no .hash (frames before hashes).</summary>
    private static string OldCopy(string root, TemplateSpec builtIn)
    {
        var dir = Path.Combine(root, TextUtil.Slug(builtIn.Name));
        Directory.CreateDirectory(dir);
        var old = TemplateSpec.Load(SaveTo(Path.Combine(root, "tmp.json"), builtIn));
        old.PtBox.Y += 46;
        old.CreditBar.Y += 44;
        old.Save(Path.Combine(dir, "template.json"));
        File.WriteAllBytes(Path.Combine(dir, "frame.png"), TestHelpers.PngBytes(8, 8));
        return dir;
    }

    private static string SaveTo(string path, TemplateSpec spec) { spec.Save(path); return path; }

    [Fact]
    public void AnOlderVersionsCopyOfABuiltIn_IsBroughtUpToDate_AndTheOldFilesAreKept()
    {
        var root = TempRoot("oldbuiltin");
        try
        {
            var builtIn = BuiltInTemplates.All().First();
            var dir = OldCopy(root, builtIn);
            var backups = Path.Combine(root, "replaced");

            Assert.True(TemplateService.UpgradeBuiltIn(dir, builtIn, backups));
            Assert.Equal(builtIn.ContentHash(), TemplateSpec.Load(Path.Combine(dir, "template.json")).ContentHash());
            Assert.False(File.Exists(Path.Combine(dir, "frame.png")));   // drawn again from today's layout on load

            var kept = Assert.Single(Directory.GetDirectories(backups));
            Assert.Equal(TestHelpers.PngBytes(8, 8), File.ReadAllBytes(Path.Combine(kept, "frame.png")));
            Assert.NotEqual(builtIn.PtBox.Y, TemplateSpec.Load(Path.Combine(kept, "template.json")).PtBox.Y);

            Assert.False(TemplateService.UpgradeBuiltIn(dir, builtIn, backups));   // once: up to date now
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ABuiltInSomeoneSavedTheirOwnChangesOver_IsLeftAlone()
    {
        var root = TempRoot("editedbuiltin");
        try
        {
            var builtIn = BuiltInTemplates.All().First();
            var dir = OldCopy(root, builtIn);
            File.WriteAllText(Path.Combine(dir, TemplateService.UserEditedTag), "x");
            var before = File.ReadAllText(Path.Combine(dir, "template.json"));

            Assert.False(TemplateService.UpgradeBuiltIn(dir, builtIn, Path.Combine(root, "replaced")));
            Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "template.json")));
            Assert.True(File.Exists(Path.Combine(dir, "frame.png")));
            Assert.False(Directory.Exists(Path.Combine(root, "replaced")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void EveryBuiltIn_AsWrittenToDisk_CountsAsUpToDate()
    {
        // Otherwise every launch would "upgrade" it again, with a backup each time.
        var root = TempRoot("builtinroundtrip");
        try
        {
            foreach (var spec in BuiltInTemplates.All())
            {
                var dir = Path.Combine(root, TextUtil.Slug(spec.Name));
                Directory.CreateDirectory(dir);
                spec.Save(Path.Combine(dir, "template.json"));
                Assert.False(TemplateService.UpgradeBuiltIn(dir, spec, Path.Combine(root, "replaced")), spec.Name);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- dragging and zooming the art ------------------------------------------------------------------

    public static IEnumerable<object[]> DragCases()
    {
        yield return new object[] { "Gold Multicolor", false };
        yield return new object[] { "Full Art", false };
        yield return new object[] { "Showcase", false };
        yield return new object[] { "Alchemist's Steel", true };   // a flip card: both halves on top of the one picture
    }

    [Theory]
    [MemberData(nameof(DragCases))]
    public void TheLiveDrag_LooksExactlyLikeTheFullRender(string frame, bool flip) => TestHelpers.RunSta(() =>
    {
        var art = Path.Combine(Path.GetTempPath(), $"cardinator-dragart-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(art, Gradient(1600, 1100));
        try
        {
            var templates = new TemplateService().LoadAll();
            var t = templates.First(x => x.Name == frame);
            var card = new CardModel
            {
                Name = "Drag Probe", ManaCost = "{2}{W}", TypeLine = "Legendary Creature — Angel", RulesText = "Flying\nLifelink",
                FlavorText = "Live.", Power = "3", Toughness = "3", SetCode = "DRG", CollectorNumber = "1", Rarity = "R",
                ArtPath = art, TemplateName = t.Name,
            };
            if (flip)
            {
                card.HalfLayout = "flip";
                card.OtherHalf = new CardModel { Name = "Other Side", TypeLine = "Creature — Spirit", Power = "5", Toughness = "5" };
            }
            var r = new CardRenderer(new SymbolService(drawnOnly: true));
            var drag = r.BeginArtDrag(card, t);
            Assert.NotNull(drag);
            foreach (var (x, y, z) in new[] { (0.0, 0.0, 1.0), (0.12, -0.08, 1.6), (-0.3, 0.2, 2.4) })
            {
                card.ArtOffsetX = x; card.ArtOffsetY = y; card.ArtScale = z;
                var full = TestHelpers.Pixels(r.RenderToBitmap(card, t, 1, previewHints: true));
                var live = TestHelpers.Pixels(r.RenderArtDrag(drag!));
                Assert.Equal(full.Length, live.Length);
                int off = 0;
                for (int i = 0; i < full.Length; i++) if (Math.Abs(full[i] - live[i]) > 24) off++;
                Assert.True(off < full.Length / 500, $"{frame} at ({x}, {y}) ×{z}: {off} of {full.Length} bytes differ");
            }
        }
        finally { try { File.Delete(art); } catch { } }
    });

    [Fact]
    public void CardsDrawnAnotherWay_RenderInFullWhileDragged() => TestHelpers.RunSta(() =>
    {
        var t = new TemplateService().LoadAll().First(x => x.Name == "Gold Multicolor");
        var r = new CardRenderer(new SymbolService(drawnOnly: true));
        var split = new CardModel { Name = "Wear", ArtPath = "a.png", HalfLayout = "split", OtherHalf = new CardModel { Name = "Tear" } };
        var meldBack = new CardModel { Name = "Melded", ArtPath = "a.png", IsBackFace = true, MeldHalf = "top" };
        Assert.Null(r.BeginArtDrag(split, t));
        Assert.Null(r.BeginArtDrag(meldBack, t));
        Assert.Null(r.BeginArtDrag(new CardModel { Name = "No art" }, t));
    });

    [Fact]
    public void DraggingTheArt_DrawsLive_ThenTheReleaseRendersTheCardInFull()
        => OnAppThread(() =>
        {
            var art = Path.Combine(Path.GetTempPath(), $"cardinator-dragwin-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(art, Gradient(900, 700));
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                main.SelectedCard!.ArtPath = art;
                main.RenderPreview();
                var live = (Image)main.FindName("LiveArtImage");
                var before = main.PreviewImage;

                Invoke(main, "NudgeArt", 0.05, 0.0);
                Invoke(main, "OnLiveFrame", null, EventArgs.Empty);
                Assert.Equal(Visibility.Visible, live.Visibility);
                Assert.IsType<DrawingImage>(live.Source);
                Assert.Same(before, main.PreviewImage);   // no full repaint per move

                main.RenderPreview();   // what the release (or the pause after a key) does
                Assert.Equal(Visibility.Collapsed, live.Visibility);
                Assert.NotSame(before, main.PreviewImage);

                // An edit other than the art's place makes the drag's overlay out of date: the next move draws it again.
                Invoke(main, "NudgeArt", 0.05, 0.0);
                Invoke(main, "OnLiveFrame", null, EventArgs.Empty);
                var first = live.Source;
                main.SelectedCard.Name = "Renamed Mid-Drag";
                Invoke(main, "NudgeArt", 0.05, 0.0);
                Invoke(main, "OnLiveFrame", null, EventArgs.Empty);
                Assert.NotSame(first, live.Source);
                var overlay = GetField<CardRenderer.ArtDrag>(main, "_artDrag");
                Assert.NotNull(overlay);
                Assert.Equal("Renamed Mid-Drag", overlay!.Card.Name);
            }
            finally { try { File.Delete(art); } catch { } }
        });

    // --- meld ----------------------------------------------------------------------------------------------

    [Fact]
    public void RemoveMeld_MakesItANormalCardAgain_SoFlipAndSplitAreBack()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            var split = (Button)main.FindName("SplitToggleBtn");
            var flip = (Button)main.FindName("FlipToggleBtn");
            var dfc = (Button)main.FindName("DfcToggleBtn");
            Assert.True(split.IsEnabled);

            ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;
            try
            {
                Invoke(main, "OnToggleMeld", null, null);
                Assert.True(card.IsMeld);
                Assert.False(split.IsEnabled);

                Invoke(main, "OnToggleMeld", null, null);
                Assert.Null(card.BackFace);
                Assert.False(card.IsDoubleFaced);
                Assert.True(split.IsEnabled);
                Assert.True(flip.IsEnabled);
                Assert.Equal("Make double-faced", dfc.Content);

                main.Undo();   // the melded card comes back
                Assert.True(main.SelectedCard!.IsMeld);
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    // --- helpers -------------------------------------------------------------------------------------------

    private static byte[] Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / w); px[i + 1] = (byte)(y * 255 / h); px[i + 2] = (byte)((x + y) % 256); px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static T? GetField<T>(object target, string name) where T : class
        => target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(target) as T;

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private static void OnAppThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri("/Cardinator;component/Theme.xaml", UriKind.Relative)));
                try { action(); }
                finally { TestHelpers.EndUiThread(); }
            }
            catch (System.Reflection.TargetInvocationException ex) { captured = ex.InnerException ?? ex; }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
