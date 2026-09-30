using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Covers template bundles (.cardframe export/import), the embedded sample templates, and the
/// --rendercards batch path.</summary>
public class TemplateBundleTests
{
    // --- .cardframe export / import round-trip -------------------------------

    [Fact]
    public void ExportThenImport_PreservesTunedSpec()
    {
        // A frame's tuned regions/name must survive a bundle round-trip (the whole point of bundles).
        var name = "Bundle Src " + Guid.NewGuid().ToString("N")[..6];
        var created = TemplateImporter.CreateFromFrame(name, SolidPngBytes(8, 8));
        var srcDir = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(created));
        var bundle = Path.Combine(Path.GetTempPath(), $"bundle-{Guid.NewGuid():N}.cardframe");
        string? importedDir = null;
        try
        {
            // Tune a region so we can prove it travels with the bundle.
            var specPath = Path.Combine(srcDir, "template.json");
            var spec = TemplateSpec.Load(specPath);
            spec.TitleBar.X = 99;
            spec.Save(specPath);

            TemplateImporter.ExportBundle(srcDir, bundle);
            Assert.True(File.Exists(bundle));

            var importedName = TemplateImporter.ImportBundle(bundle);
            Assert.Equal(name, importedName);

            // The import lands in a NEW folder (unique slug) since the source slug already exists.
            importedDir = Directory.EnumerateDirectories(AppPaths.TemplatesDir)
                .Where(d => !string.Equals(d, srcDir, StringComparison.OrdinalIgnoreCase))
                .First(d =>
                {
                    var p = Path.Combine(d, "template.json");
                    return File.Exists(p) && TemplateSpec.Load(p).Name == name;
                });

            var imported = TemplateSpec.Load(Path.Combine(importedDir, "template.json"));
            Assert.Equal(99, imported.TitleBar.X);      // tuned region preserved
            Assert.True(imported.CustomFrame);          // forced on so the frame is kept verbatim
            Assert.True(File.Exists(Path.Combine(importedDir, "frame.png")));
        }
        finally
        {
            try { File.Delete(bundle); } catch { }
            try { Directory.Delete(srcDir, true); } catch { }
            if (importedDir != null) try { Directory.Delete(importedDir, true); } catch { }
        }
    }

    [Fact]
    public void IsBundlePath_MatchesCardframeAndZip()
    {
        Assert.True(TemplateImporter.IsBundlePath("x.cardframe"));
        Assert.True(TemplateImporter.IsBundlePath("X.ZIP"));
        Assert.False(TemplateImporter.IsBundlePath("frame.png"));
    }

    [Fact]
    public void ImportBundle_MissingFiles_Throws()
    {
        var zip = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.zip");
        try
        {
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                z.CreateEntry("readme.txt");   // no template.json / frame.png
            Assert.Throws<InvalidOperationException>(() => TemplateImporter.ImportBundle(zip));
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    // --- embedded sample templates ------------------------------------------

    [Fact]
    public void SampleTemplates_ShipAndLoadByName()
    {
        var names = new TemplateService().LoadAll().Select(t => t.Name).ToHashSet();
        Assert.Contains("Alchemist's Steel", names);
        Assert.Contains("Arcane Parchment", names);
        Assert.Contains("Sealed Gate", names);
    }

    [Fact]
    public void SampleTemplates_AreCustomFrames()
    {
        var t = new TemplateService().LoadAll().First(x => x.Name == "Alchemist's Steel");
        Assert.True(t.Spec.CustomFrame);
    }

    // --- --rendercards batch path -------------------------------------------

    [Fact]
    public void RunRenderCards_RendersFolderOfCards()
        => RunSta(() =>
        {
            var inDir = Path.Combine(Path.GetTempPath(), $"cards-{Guid.NewGuid():N}");
            var outDir = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}");
            Directory.CreateDirectory(inDir);
            try
            {
                new CardModel { Name = "Alpha", TypeLine = "Instant", TemplateName = "Ocean Blue" }
                    .Save(Path.Combine(inDir, "a.json"));
                new CardModel { Name = "Beta", TypeLine = "Sorcery", TemplateName = "Alchemist's Steel" }
                    .Save(Path.Combine(inDir, "b.json"));

                var code = SelfTest.RunRenderCards(inDir, outDir, new[] { "nosym", "scale=1" });
                Assert.Equal(0, code);
                Assert.True(File.Exists(Path.Combine(outDir, "alpha.png")));
                Assert.True(File.Exists(Path.Combine(outDir, "beta.png")));
            }
            finally
            {
                try { Directory.Delete(inDir, true); } catch { }
                try { Directory.Delete(outDir, true); } catch { }
            }
        });

    // --- helpers ------------------------------------------------------------

    private static byte[] SolidPngBytes(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 80; px[i + 2] = 160; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
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
