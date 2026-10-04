using System;
using System.IO;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Covers the per-set-folder portability fixes: both art AND set-symbol images travel with a saved set
/// (relative on disk, resolved on load, copied into the folder), shared images are copied once, and the
/// collector-number duplicate check sees through mixed "5" / "005/20" numbering.
/// </summary>
public class SetFolderTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "set_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 });
        return path;
    }

    // --- CardProject: both image paths round-trip (H1 set symbol travels) ----

    [Fact]
    public void MakeArtRelative_And_ResolveArt_RoundTripBothArtAndSetSymbol()
    {
        var folder = TempDir();
        try
        {
            var card = new CardModel
            {
                Name = "X",
                ArtPath = Path.GetFullPath(Path.Combine(folder, "art", "pic.png")),
                SetSymbolPath = Path.GetFullPath(Path.Combine(folder, "art", "sym.png")),
            };

            CardProject.MakeArtRelative(card, folder);
            Assert.False(Path.IsPathRooted(card.ArtPath));
            Assert.False(Path.IsPathRooted(card.SetSymbolPath));
            Assert.Equal(Path.Combine("art", "pic.png"), card.ArtPath);
            Assert.Equal(Path.Combine("art", "sym.png"), card.SetSymbolPath);

            CardProject.ResolveArt(card, folder);
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, "art", "pic.png")), card.ArtPath);
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, "art", "sym.png")), card.SetSymbolPath);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public void MakeArtRelative_LeavesSetSymbolOutsideTheFolderAbsolute()
    {
        var folder = TempDir();
        var outside = Path.Combine(Path.GetTempPath(), "shared", "sym.png");
        try
        {
            var card = new CardModel { Name = "X", SetSymbolPath = outside };
            CardProject.MakeArtRelative(card, folder);
            Assert.Equal(outside, card.SetSymbolPath);   // untouched (not inside the set)
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // --- SetFolder.LocalizeImages (H1 copy + M11 dedupe/robustness) ----------

    [Fact]
    public void LocalizeImages_CopiesExternalArtAndSymbolIntoTheSetAndRepoints()
    {
        var folder = TempDir();
        var ext = TempDir();
        try
        {
            var art = WritePng(Path.Combine(ext, "dragon.png"));
            var sym = WritePng(Path.Combine(ext, "setsym.png"));
            var card = new CardModel { Name = "D", ArtPath = art, SetSymbolPath = sym };

            var stranded = SetFolder.LocalizeImages(new[] { card }, folder);

            Assert.Equal(0, stranded);
            var artRoot = Path.GetFullPath(Path.Combine(folder, "art")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(artRoot, Path.GetFullPath(card.ArtPath));
            Assert.StartsWith(artRoot, Path.GetFullPath(card.SetSymbolPath));
            Assert.True(File.Exists(card.ArtPath));
            Assert.True(File.Exists(card.SetSymbolPath));
        }
        finally { try { Directory.Delete(folder, true); } catch { } try { Directory.Delete(ext, true); } catch { } }
    }

    [Fact]
    public void LocalizeImages_CopiesASharedSymbolOnlyOnce()
    {
        var folder = TempDir();
        var ext = TempDir();
        try
        {
            var sym = WritePng(Path.Combine(ext, "shared.png"));
            var a = new CardModel { Name = "A", SetSymbolPath = sym };
            var b = new CardModel { Name = "B", SetSymbolPath = sym };

            SetFolder.LocalizeImages(new[] { a, b }, folder);

            Assert.Equal(a.SetSymbolPath, b.SetSymbolPath);                 // both point at the one copy
            var files = Directory.GetFiles(Path.Combine(folder, "art"));
            Assert.Single(files);                                          // copied once, not per-card
        }
        finally { try { Directory.Delete(folder, true); } catch { } try { Directory.Delete(ext, true); } catch { } }
    }

    [Fact]
    public void LocalizeImages_LeavesImagesAlreadyInsideTheSetAlone()
    {
        var folder = TempDir();
        try
        {
            var inside = WritePng(Path.Combine(folder, "art", "already.png"));
            var card = new CardModel { Name = "C", ArtPath = inside };

            SetFolder.LocalizeImages(new[] { card }, folder);

            Assert.Equal(inside, card.ArtPath);                            // unchanged
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "art"))); // no duplicate copy
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public void LocalizeImages_LeavesAMissingFilePathUnchanged()
    {
        var folder = TempDir();
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".png");
            var card = new CardModel { Name = "M", ArtPath = missing };

            var stranded = SetFolder.LocalizeImages(new[] { card }, folder);

            Assert.Equal(0, stranded);            // missing files aren't "stranded" — the art-blank check owns that
            Assert.Equal(missing, card.ArtPath);  // left as-is
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // --- collector-number normalization (M3) --------------------------------

    [Theory]
    [InlineData("5", "5")]
    [InlineData("005", "5")]
    [InlineData("005/20", "5")]
    [InlineData("1/9", "1")]
    [InlineData("  007/300  ", "7")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("★", "★")]
    public void NormalizeCollector_ReducesToComparableIdentity(string? input, string expected)
        => Assert.Equal(expected, CardValidator.NormalizeCollector(input));

    [Fact]
    public void DupCollector_FlagsMixedFormatsThatAreTheSameNumber()
    {
        var spec = new TemplateSpec { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };
        var a = new CardModel { Name = "A", CollectorNumber = "5" };
        var b = new CardModel { Name = "B", CollectorNumber = "005/20" };
        var all = new[] { a, b };

        var issues = CardValidator.Validate(a, spec, all);

        Assert.Contains(issues, i => i.Code == "dup-collector");
    }

    [Fact]
    public void DupCollector_DoesNotFlagDistinctAutoNumbers()
    {
        var spec = new TemplateSpec { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };
        var a = new CardModel { Name = "A", CollectorNumber = "001/12" };
        var b = new CardModel { Name = "B", CollectorNumber = "002/12" };

        var issues = CardValidator.Validate(a, spec, new[] { a, b });

        Assert.DoesNotContain(issues, i => i.Code == "dup-collector");
    }
}
