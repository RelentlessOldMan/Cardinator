using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The loose ends left after both reviews (1.6.17): a back face with no frame on a print sheet, a frame bundle
/// dropped onto the window, and a picture frame that can't be read for a moment. Each test fails with its fix taken out. In the STA-window collection because one
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
