using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Renderer defects found in the fresh-eyes review: badged rows (planeswalker / saga / class) dropping the
/// font's shadow outline, a typo-sized Class level crashing the render, planeswalker text running under the
/// loyalty shield, the full-art scrim not covering the box the text is laid out in, and the pixel inspector
/// crying "border too thin" at a footer that legitimately rides on the border.
/// </summary>
public class RendererCorrectnessTests
{
    /// <summary>Renders against a spec with a transparent 1x1 frame, so what we assert about is the
    /// renderer's own drawing (text, scrims, badges) and not a baked frame image.</summary>
    private static BitmapSource Render(CardModel card, TemplateSpec spec)
    {
        var blank = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 0 }, 4);
        var tpl = new Template { Name = spec.Name, Spec = spec, FramePath = "", FrameImage = blank };
        return new CardRenderer(new SymbolService()).RenderToBitmap(card, tpl, supersample: 1);
    }

    /// <summary>A solid-white PNG on disk, so a scrim drawn over it is measurable.</summary>
    private static string WhitePngFile(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(Path.GetTempPath(), "white_" + Guid.NewGuid().ToString("N") + ".png");
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static TemplateSpec FullArtSpec()
    {
        var spec = new TemplateSpec { Name = "FA", FullArt = true };
        foreach (var f in new[] { spec.TitleFont, spec.TypeFont, spec.RulesFont, spec.FlavorFont, spec.PtFont })
        { f.Color = "#FFFFFF"; f.Shadow = true; }
        spec.Normalize();
        return spec;
    }

    // --- C1: badged rows honour the font's shadow outline ----------------------

    [Fact]
    public void SagaChapters_DrawTheFontsShadowOutline()
    {
        TestHelpers.RunSta(() =>
        {
            var card = new CardModel
            {
                Name = "Chronicle", TypeLine = "Enchantment — Saga",
                RulesText = "I — Draw a card.\nII — Create a token.\nIII — Exile a creature.",
            };

            var withShadow = FullArtSpec();
            var without = FullArtSpec();
            foreach (var f in new[] { without.RulesFont }) f.Shadow = false;

            // The chapter text is the only difference, so a shadow that isn't drawn makes these identical.
            Assert.NotEqual(TestHelpers.Pixels(Render(card, withShadow)), TestHelpers.Pixels(Render(card, without)));
        });
    }

    // --- C2: a silly level number must not abort the render --------------------

    [Theory]
    [InlineData("2147483648")]      // one past int.MaxValue
    [InlineData("999999999999")]
    public void AClassLevelTooBigForAnInt_StillRenders(string level)
    {
        TestHelpers.RunSta(() =>
        {
            var card = new CardModel
            {
                Name = "Scholar", TypeLine = "Enchantment — Class",
                RulesText = $"Gain a thing.\n{{2}}: Level {level}\nGain another thing.",
            };
            var spec = new TemplateSpec { Name = "T" };
            spec.Normalize();

            var bmp = Render(card, spec);   // used to throw OverflowException out of the whole render
            Assert.True(TestHelpers.HasContent(bmp));
        });
    }

    // --- C3: planeswalker abilities wrap around the loyalty shield -------------

    [Fact]
    public void PlaneswalkerAbilityText_DoesNotRunUnderTheLoyaltyShield()
    {
        TestHelpers.RunSta(() =>
        {
            // A custom frame can put the P/T box (and so the loyalty shield) anywhere in the layout — the
            // frame designer lets the user drag it. Here it sits well inside the text box, which is the case
            // that makes the overlap unmistakable; with the stock frame the shield only clips the last line.
            var spec = new TemplateSpec { Name = "T", CustomFrame = true };
            spec.PtBox = new Region { X = 470, Y = 760, W = 180, H = 110 };
            spec.Normalize();
            const string abilities =
                "+2: Draw a card and then discard a card at random from your hand, then scry two and gain life.\n"
                + "+1: Create a 2/2 white Knight creature token with vigilance and lifelink until end of turn.\n"
                + "−1: Target creature gets +3/+3 and gains trample until the end of your next turn, then untap.\n"
                + "−3: Destroy target nonland permanent an opponent controls, then draw a card and gain two life.\n"
                + "−7: Exile all nonland permanents your opponents control until this planeswalker leaves play.";

            var card = new CardModel { Name = "Walker", TypeLine = "Legendary Planeswalker — Test", RulesText = abilities, Loyalty = "4" };

            // The shield is drawn OVER the abilities, so an overlap isn't ugly — it silently eats words.
            // Assert it geometrically, against the layout the renderer will actually draw.
            var (runs, shield) = new CardRenderer(new SymbolService()).InspectPlaneswalkerLayout(card, spec);
            Assert.NotEmpty(runs);
            var band = runs.Where(r => r.Bottom > shield.Top && r.Y < shield.Bottom).ToList();
            Assert.NotEmpty(band);   // the scenario is only meaningful if text shares the shield's rows

            var hidden = runs.Where(r => r.IntersectsWith(shield)).ToList();
            Assert.True(hidden.Count == 0,
                $"{hidden.Count} ability text run(s) land under the loyalty shield {shield}, e.g. {hidden.FirstOrDefault()}");
        });
    }

    // --- C4: the full-art scrim covers the box text is laid out in -------------

    [Fact]
    public void FullArtScrim_CoversTheTextBoxExtension_WhenTheFooterIsOnTheBorder()
    {
        TestHelpers.RunSta(() =>
        {
            var spec = FullArtSpec();
            spec.FooterPlacement = "border";      // EffectiveTextBox now extends below TextBox
            spec.Normalize();

            // Pure white art fills the card, so the scrim is the only thing that can darken this area.
            var art = WhitePngFile(64, 64);
            try
            {
                var card = new CardModel
                {
                    Name = "Pale", TypeLine = "Instant", ArtPath = art,
                    RulesText = string.Join(" ", Enumerable.Repeat("word", 90)),
                };
                var bmp = Render(card, spec);

                var eff = spec.EffectiveTextBox;
                var raw = spec.TextBox;
                Assert.True(eff.Y + eff.H > raw.Y + raw.H, "this template should extend the text box");

                // Sample inside the extension band (between the old TextBox bottom and the effective bottom).
                int y = (int)(raw.Y + raw.H + (eff.Y + eff.H - (raw.Y + raw.H)) / 2);
                int x = (int)(eff.X + eff.W / 2);
                var px = new byte[4];
                new CroppedBitmap(bmp, new System.Windows.Int32Rect(x, y, 1, 1)).CopyPixels(px, 4, 0);
                // Scrimmed white art is clearly darkened; unscrimmed it would still be near-white.
                Assert.True(px[0] < 220 && px[1] < 220 && px[2] < 220,
                    $"text-box extension was not scrimmed (pixel {px[2]},{px[1]},{px[0]})");
            }
            finally { try { File.Delete(art); } catch { } }
        });
    }

    // --- C5: a footer riding on the border is not a thin border ----------------

    [Fact]
    public void FooterOnTheBorder_IsNotReportedAsAThinBorder()
    {
        TestHelpers.RunSta(() =>
        {
            var spec = new TemplateSpec { Name = "T", FooterPlacement = "border" };
            spec.Normalize();
            var card = new CardModel
            {
                Name = "Longfooter", TypeLine = "Creature — Human",
                CollectorNumber = "001/999", SetCode = "LONG", Rarity = "mythic",
                Artist = "A Very Long Artist Name Indeed", Copyright = "™ & © 2026 Nobody At All",
            };

            var issues = RenderInspector.Inspect(Render(card, spec), card, spec);
            Assert.DoesNotContain(issues, i => i.Code == "border-thin" && i.Message.Contains("bottom"));
        });
    }

    [Fact]
    public void AMissingBottomBorder_IsStillReported()   // the C5 fix must not blind the check
    {
        TestHelpers.RunSta(() =>
        {
            var spec = new TemplateSpec { Name = "T", FooterPlacement = "border" };
            spec.Normalize();
            var card = new CardModel { Name = "X", TypeLine = "Instant" };
            var good = Render(card, spec);

            // Paint the bottom rows white to simulate the border going missing.
            var w = good.PixelWidth; var h = good.PixelHeight;
            int stride = w * 4;
            var px = TestHelpers.Pixels(good);
            int band = (int)(spec.BorderThickness) + 4;
            for (int y = h - band; y < h; y++)
                for (int x = 0; x < w; x++)
                { int i = y * stride + x * 4; px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
            var tampered = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);

            var issues = RenderInspector.Inspect(tampered, card, spec);
            Assert.Contains(issues, i => i.Code == "border-thin" && i.Message.Contains("bottom"));
        });
    }

    // --- C7: a bundle whose zip entries use backslashes still imports ----------

    [Fact]
    public void ImportBundle_FindsEntriesWrittenWithBackslashSeparators()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "bundle_" + Guid.NewGuid().ToString("N") + ".cardframe");
        string? installed = null;
        try
        {
            var spec = new TemplateSpec { Name = "Backslash " + Guid.NewGuid().ToString("N")[..6] };
            var specFile = Path.Combine(Path.GetTempPath(), "spec_" + Guid.NewGuid().ToString("N") + ".json");
            spec.Save(specFile);
            var specJson = File.ReadAllText(specFile);
            File.Delete(specFile);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // Entry names with '\' — written by some Windows zip tools; Name comes back empty for these.
                var e1 = zip.CreateEntry("sub\\template.json");
                using (var w = new StreamWriter(e1.Open())) w.Write(specJson);
                var e2 = zip.CreateEntry("sub\\frame.png");
                using (var b = new BinaryWriter(e2.Open())) b.Write(TestHelpers.PngBytes(4, 4));
            }

            var name = TemplateImporter.ImportBundle(zipPath);
            installed = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(name));
            Assert.True(File.Exists(Path.Combine(installed, "frame.png")));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { if (installed != null) Directory.Delete(installed, true); } catch { }
        }
    }
}
