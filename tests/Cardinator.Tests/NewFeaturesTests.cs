using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Covers the logic added in the editor-UX pass: portable per-set art paths, the "fill details from
/// Scryfall without touching name/art/frame" contract, the browser-paste black-image repair, built-in
/// frame protection, bundled-slug discovery, and unique template-folder naming.
/// </summary>
public class NewFeaturesTests
{
    // --- portable per-set art paths (CardProject) ---------------------------

    [Fact]
    public void RelativeArtPath_MakesArtInsideTheSetFolderRelative()
    {
        var folder = Path.Combine(Path.GetTempPath(), "set_" + Guid.NewGuid().ToString("N"));
        var art = Path.Combine(folder, "art", "dragon.png");

        var rel = CardProject.RelativeArtPath(art, folder);

        Assert.False(Path.IsPathRooted(rel));
        Assert.Equal(Path.Combine("art", "dragon.png"), rel);
    }

    [Fact]
    public void RelativeArtPath_LeavesArtOutsideTheSetFolderAbsolute()
    {
        var folder = Path.Combine(Path.GetTempPath(), "set_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "dragon.png");

        var rel = CardProject.RelativeArtPath(outside, folder);

        Assert.Equal(outside, rel);   // untouched — not inside the set
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RelativeArtPath_PassesThroughEmpty(string? art)
        => Assert.Equal("", CardProject.RelativeArtPath(art, @"C:\set"));

    [Fact]
    public void ResolveArtPath_TurnsRelativeBackIntoAbsoluteUnderTheFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "set_" + Guid.NewGuid().ToString("N"));

        var abs = CardProject.ResolveArtPath(Path.Combine("art", "dragon.png"), folder);

        Assert.True(Path.IsPathRooted(abs));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "art", "dragon.png")), abs);
    }

    [Fact]
    public void ResolveArtPath_LeavesAbsolutePathsUnchanged()
    {
        var abs = Path.Combine(Path.GetTempPath(), "x", "dragon.png");
        Assert.Equal(abs, CardProject.ResolveArtPath(abs, @"C:\set"));
    }

    [Fact]
    public void ArtPath_RoundTripsThroughRelativeAndBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "set_" + Guid.NewGuid().ToString("N"));
        var original = Path.GetFullPath(Path.Combine(folder, "art", "pic.png"));

        var rel = CardProject.RelativeArtPath(original, folder);
        var back = CardProject.ResolveArtPath(rel, folder);

        Assert.Equal(original, back);
    }

    // --- fill details without touching identity (CardDetailsFill) ------------

    [Fact]
    public void ApplyScryfall_FillsDetailsButNeverNameArtOrFrame()
    {
        var target = new CardModel
        {
            Name = "Boogie Woogie",
            ArtPath = @"C:\my\custom-art.png",
            TemplateName = "Gold Multicolor",
            ManaCost = "", TypeLine = "", RulesText = "",
        };
        var face = new CardModel
        {
            Name = "Lightning Bolt",
            ArtPath = @"C:\scryfall\bolt.png",
            TemplateName = "Crimson Red",
            ManaCost = "{R}",
            TypeLine = "Instant",
            RulesText = "Lightning Bolt deals 3 damage to any target.",
            Power = "9", Toughness = "9", Loyalty = "4",
            SetCode = "LEA", CollectorNumber = "161", Rarity = "C",
        };

        CardDetailsFill.ApplyScryfall(target, face);

        // Identity preserved.
        Assert.Equal("Boogie Woogie", target.Name);
        Assert.Equal(@"C:\my\custom-art.png", target.ArtPath);
        Assert.Equal("Gold Multicolor", target.TemplateName);

        // Details filled.
        Assert.Equal("{R}", target.ManaCost);
        Assert.Equal("Instant", target.TypeLine);
        Assert.Equal("Lightning Bolt deals 3 damage to any target.", target.RulesText);
        Assert.Equal("9", target.Power);
        Assert.Equal("9", target.Toughness);
        Assert.Equal("4", target.Loyalty);
        Assert.Equal("LEA", target.SetCode);
        Assert.Equal("161", target.CollectorNumber);
        Assert.Equal("C", target.Rarity);
    }

    // --- browser-paste "black image" repair (ImageIntake.RepairZeroAlpha) ----

    [Fact]
    public void RepairZeroAlpha_MakesAnAllTransparentImageOpaque()
    {
        TestHelpers.RunSta(() =>
        {
            int w = 4, h = 4, stride = w * 4;
            var px = new byte[h * stride];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = 10; px[i + 1] = 120; px[i + 2] = 240; px[i + 3] = 0;   // real RGB, alpha = 0
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);

            var fixedBmp = ImageIntake.RepairZeroAlpha(src);

            var outPx = TestHelpers.Pixels(fixedBmp);
            Assert.All(Enumerable.Range(0, outPx.Length / 4), i => Assert.Equal(255, outPx[i * 4 + 3]));
            // RGB preserved.
            Assert.Equal(10, outPx[0]);
            Assert.Equal(120, outPx[1]);
            Assert.Equal(240, outPx[2]);
        });
    }

    [Fact]
    public void RepairZeroAlpha_LeavesAnImageWithRealAlphaUnchanged()
    {
        TestHelpers.RunSta(() =>
        {
            int w = 2, h = 2, stride = w * 4;
            var px = new byte[h * stride];
            for (int i = 0; i < px.Length; i += 4) { px[i + 2] = 200; px[i + 3] = 128; }   // half-transparent
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);

            var result = ImageIntake.RepairZeroAlpha(src);

            var outPx = TestHelpers.Pixels(result);
            Assert.All(Enumerable.Range(0, outPx.Length / 4), i => Assert.Equal(128, outPx[i * 4 + 3]));
        });
    }

    // --- built-in frame protection (TemplateService / SampleTemplates) -------

    [Fact]
    public void IsBuiltIn_TrueForShippedProceduralFrames()
    {
        Assert.True(TemplateService.IsBuiltIn("gold-multicolor"));
        Assert.True(TemplateService.IsBuiltIn("Gold-Multicolor"));   // case-insensitive
    }

    [Fact]
    public void IsBuiltIn_FalseForAUserFrame()
        => Assert.False(TemplateService.IsBuiltIn("my-custom-frame-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void BundledSlugs_AreAllProtectedBuiltIns()
    {
        var bundled = SampleTemplates.BundledSlugs();
        Assert.NotEmpty(bundled);
        Assert.All(bundled, slug => Assert.True(TemplateService.IsBuiltIn(slug)));
    }

    // --- unique template folder naming (TemplateImporter) --------------------

    [Fact]
    public void UniqueTemplateDir_AppendsSuffixWhenTheSlugExists()
    {
        var slug = "zz_test_" + Guid.NewGuid().ToString("N");
        var first = TemplateImporter.UniqueTemplateDir(slug);
        Assert.EndsWith(slug, first);

        Directory.CreateDirectory(first);
        try
        {
            var second = TemplateImporter.UniqueTemplateDir(slug);
            Assert.NotEqual(first, second);
            Assert.EndsWith(slug + "-2", second);
        }
        finally { try { Directory.Delete(first, true); } catch { } }
    }
}
