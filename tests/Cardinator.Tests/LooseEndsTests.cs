using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The loose ends left after both reviews (1.6.17): a back face with no frame on a print sheet, a frame bundle
/// dropped onto the window, and a picture frame that can't be read for a moment. 1.6.19: a frame set aside by mistake
/// before that comes back, and Fill blanks gives a new back face the set's defaults. Each test fails with its fix
/// taken out. In the STA-window collection because one
/// builds a <see cref="Cardinator.MainWindow"/>.
/// </summary>
[Collection("STAWindows")]
public class LooseEndsTests
{
    // --- print sheets: a back face with no frame of its own --------------------------------------------

    private static readonly PageSpec TwoUp = new() { Width = 1500, Height = 1050, Cols = 2, Rows = 1, CutMarks = false };

    private static (IReadOnlyList<Template> templates, string frontFrame) Frames()
    {
        var templates = new TemplateService().LoadAll();
        // Anything but the first installed frame, which is what the back used to get.
        return (templates, templates.First(t => t.Name != templates[0].Name).Name);
    }

    private static CardModel DoubleFaced(string frontFrame, string backFrame) => new()
    {
        Name = "Front", TypeLine = "Creature — Human", Power = "2", Toughness = "2", TemplateName = frontFrame,
        BackFace = new CardModel { Name = "Back", TypeLine = "Creature — Werewolf", Power = "4", Toughness = "4", TemplateName = backFrame },
    };

    private static byte[] Pixels(BitmapSource page) => TestHelpers.Pixels(page);

    [Fact]
    public void OnASheet_ABackWithNoFrame_WearsItsFrontsFrame() => TestHelpers.RunSta(() =>
    {
        var (templates, frontFrame) = Frames();
        var symbols = new SymbolService();
        BitmapSource Sheet(string backFrame)
            => SheetExporter.Compose(BatchService.ExpandFaces(new[] { DoubleFaced(frontFrame, backFrame) }), templates, symbols, TwoUp).Single();

        var expected = Pixels(Sheet(frontFrame));
        Assert.NotEqual(expected, Pixels(Sheet(templates[0].Name)));   // the two frames really look different
        Assert.Equal(expected, Pixels(Sheet("")));
    });

    [Fact]
    public void OnADoubleSidedSheet_ABackWithNoFrame_WearsItsFrontsFrame() => TestHelpers.RunSta(() =>
    {
        var (templates, frontFrame) = Frames();
        var symbols = new SymbolService();
        var genericBack = BackRenderer.Render(supersample: 1);
        BitmapSource BackPage(string backFrame)
            => SheetExporter.ComposeDoubleSided(new[] { DoubleFaced(frontFrame, backFrame) }, templates, symbols, TwoUp, genericBack).ElementAt(1);

        var expected = Pixels(BackPage(frontFrame));
        Assert.NotEqual(expected, Pixels(BackPage(templates[0].Name)));
        Assert.Equal(expected, Pixels(BackPage("")));
    });

    // --- a frame bundle dropped onto the window ------------------------------------------------------

    [Fact]
    public void ACardframeDroppedOnTheWindow_InstallsTheFrame()
        => OnAppThread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"cardinator-dropframe-{Guid.NewGuid():N}");
            var name = "Dropped " + Guid.NewGuid().ToString("N")[..6];
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            string? installed = null;
            try
            {
                new TemplateSpec { Name = name, CustomFrame = true }.Save(Path.Combine(src, "template.json"));
                File.WriteAllBytes(Path.Combine(src, "frame.png"), TestHelpers.PngBytes(75, 105));
                var bundle = Path.Combine(root, "shared.cardframe");
                TemplateImporter.ExportBundle(src, bundle);

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Assert.DoesNotContain(main.Templates, t => t.Name == name);
                var drop = (Task)main.GetType().GetMethod("DropFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(main, new object[] { new[] { bundle }, new Point(0, 0) })!;
                drop.GetAwaiter().GetResult();

                installed = main.Templates.FirstOrDefault(t => t.Name == name)?.FramePath is { } fp ? Path.GetDirectoryName(fp) : null;
                Assert.Contains(main.Templates, t => t.Name == name);
                Assert.Equal(name, main.SelectedTemplate?.Name);
                Assert.Contains($"Imported template \"{name}\"", main.Status);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                if (installed != null) try { Directory.Delete(installed, true); } catch { }
            }
        });

    // --- a picture frame that can't be read right now --------------------------------------------------

    [Fact]
    public void APictureFrameThatIsBrieflyLocked_IsLeftAlone_AndLoadsOnceItIsFree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cardinator-lockedframe-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "mine");
        Directory.CreateDirectory(dir);
        try
        {
            new TemplateSpec { Name = "Mine", CustomFrame = true }.Save(Path.Combine(dir, "template.json"));
            var framePath = Path.Combine(dir, "frame.png");
            var picture = TestHelpers.PngBytes(75, 105);
            File.WriteAllBytes(framePath, picture);

            // Locked against reading, as a sync client or virus scanner may hold it for a moment (moving it is still allowed).
            using (new FileStream(framePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
            {
                IReadOnlyList<Template> whileLocked = [];
                TestHelpers.RunSta(() => whileLocked = new TemplateService().LoadFrom(root));
                Assert.DoesNotContain(whileLocked, t => t.Name == "Mine");   // skipped this time…
            }
            Assert.Equal(picture, File.ReadAllBytes(framePath));             // …but not set aside or drawn over
            Assert.Equal(new[] { "frame.png", "template.json" }, Directory.GetFiles(dir).Select(Path.GetFileName).Order());

            IReadOnlyList<Template> later = [];
            TestHelpers.RunSta(() => later = new TemplateService().LoadFrom(root));
            Assert.Contains(later, t => t.Name == "Mine");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RunningOutOfMemory_IsNotTakenForACorruptFrame()
    {
        Assert.True(TemplateService.IsPassingReadFailure(new OutOfMemoryException()));
        Assert.True(TemplateService.IsPassingReadFailure(new IOException("The file is in use by another process.")));
        Assert.False(TemplateService.IsPassingReadFailure(new NotSupportedException("No imaging component suitable")));
        Assert.False(TemplateService.IsPassingReadFailure(new FileFormatException()));
    }

    // --- a picture frame set aside by mistake comes back (1.6.19) --------------------------------------

    private const string SetAsideName = "frame.png.corrupt-20261007-170419";   // as an earlier version named it

    /// <summary>A picture-frame folder: its spec, and the user's picture (not yet written anywhere).</summary>
    private static (string root, string dir, TemplateSpec spec, byte[] picture) PictureFrame(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "mine");
        Directory.CreateDirectory(dir);
        var spec = new TemplateSpec { Name = "Mine", CustomFrame = true };
        spec.Save(Path.Combine(dir, "template.json"));
        return (root, dir, spec, TestHelpers.PngBytes(75, 105, 0xFF8A2BE2));
    }

    private static IReadOnlyList<Template> Load(string root)
    {
        IReadOnlyList<Template> loaded = [];
        TestHelpers.RunSta(() => loaded = new TemplateService().LoadFrom(root));
        return loaded;
    }

    [Fact]
    public void AFrameSetAsideByMistake_ComesBack_InPlaceOfItsPlaceholder()
    {
        var (root, dir, spec, picture) = PictureFrame("restore");
        try
        {
            var framePath = Path.Combine(dir, "frame.png");
            File.WriteAllBytes(Path.Combine(dir, SetAsideName), picture);                // set aside over a passing error…
            TestHelpers.RunSta(() => FrameGenerator.Generate(spec, framePath));          // …and the placeholder drawn instead

            Assert.Contains(Load(root), t => t.Name == "Mine");
            Assert.Equal(picture, File.ReadAllBytes(framePath));
            Assert.False(File.Exists(Path.Combine(dir, SetAsideName)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AFrameSetAsideByMistake_ComesBack_WhenNothingTookItsPlace()
    {
        var (root, dir, _, picture) = PictureFrame("restore-missing");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, SetAsideName), picture);
            Assert.Contains(Load(root), t => t.Name == "Mine");
            Assert.Equal(picture, File.ReadAllBytes(Path.Combine(dir, "frame.png")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASetAsideFrame_NeverReplacesAFrameTheUserPutThereSince()
    {
        var (root, dir, _, picture) = PictureFrame("restore-mine");
        try
        {
            var theirNewFrame = TestHelpers.PngBytes(75, 105, 0xFF20B2AA);
            File.WriteAllBytes(Path.Combine(dir, SetAsideName), picture);
            File.WriteAllBytes(Path.Combine(dir, "frame.png"), theirNewFrame);

            Load(root);
            Assert.Equal(theirNewFrame, File.ReadAllBytes(Path.Combine(dir, "frame.png")));
            Assert.Equal(picture, File.ReadAllBytes(Path.Combine(dir, SetAsideName.Replace(".corrupt-", ".kept-"))));   // 1.6.20: kept
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AReallyCorruptSetAsideFile_StaysSetAside()
    {
        var (root, dir, spec, _) = PictureFrame("restore-corrupt");
        try
        {
            var framePath = Path.Combine(dir, "frame.png");
            var garbage = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(dir, SetAsideName), garbage);
            TestHelpers.RunSta(() => FrameGenerator.Generate(spec, framePath));
            var placeholder = File.ReadAllBytes(framePath);

            Load(root);
            Assert.Equal(placeholder, File.ReadAllBytes(framePath));
            Assert.Equal(garbage, File.ReadAllBytes(Path.Combine(dir, SetAsideName)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- Fill blanks: a back face it adds gets the set's defaults (1.6.19) ---------------------------

    [Fact]
    public void FillBlanks_GivesTheBackFaceItAddsTheSetsDefaults()
        => OnAppThread(() =>
        {
            ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "object": "list", "not_found": [], "data": [ { "object": "card", "name": "Delver of Secrets // Insectile Aberration",
                  "layout": "transform", "set": "isd", "collector_number": "51", "rarity": "common", "card_faces": [
                    { "name": "Delver of Secrets", "mana_cost": "{U}", "type_line": "Creature — Human Wizard", "oracle_text": "Transform it.", "power": "1", "toughness": "1" },
                    { "name": "Insectile Aberration", "mana_cost": "", "type_line": "Creature — Human Insect", "oracle_text": "Flying", "power": "3", "toughness": "2" } ] } ] }
                """, Encoding.UTF8, "application/json"),
            })));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                main.GetType().GetField("_setProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(main, new SetProfile { Copyright = "© My Custom Set" });
                var card = new CardModel { Name = "Delver of Secrets", TemplateName = main.Templates[0].Name };   // name only: needs filling
                main.Cards.Add(card);

                main.GetType().GetMethod("OnLookupMissing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(main, new object?[] { null, null });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (main.Busy && sw.ElapsedMilliseconds < 15000)
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(10);
                }

                Assert.False(main.Busy, "the fill didn't finish");
                Assert.Equal("Insectile Aberration", card.BackFace?.Name);
                Assert.Equal("© My Custom Set", card.BackFace!.Copyright);
            }
            finally { ScryfallClient.TestHttp = null; }
        });

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

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
