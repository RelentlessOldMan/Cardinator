using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Covers the validation-surfacing fixes (missing frame, missing set symbol, stray loyalty) and the
/// "Share set + frames" packager that makes a set portable to another person.
/// </summary>
public class ValidationAndPackagingTests
{
    private static TemplateSpec Spec() => new() { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pkg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // --- H2: missing-frame check -------------------------------------------

    [Fact]
    public void MissingFrame_IsFlagged_WhenTemplateNotInstalled()
    {
        var card = new CardModel { Name = "A", TemplateName = "Nonexistent Frame" };
        var issues = CardValidator.Validate(card, Spec(), null, new[] { "Gold Multicolor", "Ocean Blue" });
        Assert.Contains(issues, i => i.Code == "frame-missing");
    }

    [Fact]
    public void MissingFrame_NotFlagged_WhenInstalledOrUnknownList()
    {
        var card = new CardModel { Name = "A", TemplateName = "Ocean Blue" };
        Assert.DoesNotContain(CardValidator.Validate(card, Spec(), null, new[] { "Ocean Blue" }),
            i => i.Code == "frame-missing");
        // No installed-list passed → the check is skipped (back-compat).
        Assert.DoesNotContain(CardValidator.Validate(card, Spec()),
            i => i.Code == "frame-missing");
    }

    // --- M13: missing set-symbol check -------------------------------------

    [Fact]
    public void MissingSetSymbol_IsFlagged()
    {
        var card = new CardModel { Name = "A", SetSymbolPath = Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N") + ".png") };
        Assert.Contains(CardValidator.Validate(card, Spec()), i => i.Code == "symbol-missing");
    }

    [Fact]
    public void PresentSetSymbol_IsNotFlagged()
    {
        var dir = TempDir();
        try
        {
            var sym = Path.Combine(dir, "sym.png"); Write(sym);
            var card = new CardModel { Name = "A", SetSymbolPath = sym };
            Assert.DoesNotContain(CardValidator.Validate(card, Spec()), i => i.Code == "symbol-missing");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- M8: stray loyalty hides P/T ---------------------------------------

    [Fact]
    public void StrayLoyalty_OnNonPlaneswalker_IsFlagged()
    {
        var card = new CardModel { Name = "A", TypeLine = "Creature — Bear", Power = "2", Toughness = "2", Loyalty = "4" };
        Assert.Contains(CardValidator.Validate(card, Spec()), i => i.Code == "loyalty-nonplaneswalker");
    }

    [Fact]
    public void Loyalty_OnPlaneswalker_IsFine()
    {
        var card = new CardModel { Name = "A", TypeLine = "Legendary Planeswalker — Jace", Loyalty = "4" };
        Assert.DoesNotContain(CardValidator.Validate(card, Spec()), i => i.Code == "loyalty-nonplaneswalker");
    }

    // --- Share set + frames packager ---------------------------------------

    [Fact]
    public void ExportSetWithFrames_IncludesProjectArtAndCustomFramesButNotBuiltins()
    {
        var setDir = TempDir();
        var templatesDir = TempDir();
        var dest = Path.Combine(TempDir(), "share.zip");
        try
        {
            // A saved set: project file + art/.
            var project = Path.Combine(setDir, "MySet.cardinator"); Write(project, "{}");
            Write(Path.Combine(setDir, "art", "pic.png"));
            Write(Path.Combine(setDir, "out", "render.png"));   // should NOT be included

            // A custom frame the cards reference, plus a built-in that must be skipped.
            Write(Path.Combine(templatesDir, "my-frame", "template.json"), "{}");
            Write(Path.Combine(templatesDir, "my-frame", "frame.png"));
            Write(Path.Combine(templatesDir, "gold-multicolor", "template.json"), "{}");
            Write(Path.Combine(templatesDir, "gold-multicolor", "frame.png"));

            int frames = SetPackager.ExportSetWithFrames(
                project, new[] { "My Frame", "Gold Multicolor", "My Frame" }, templatesDir, dest);

            Assert.Equal(1, frames);   // only the custom frame, counted once
            using var zip = ZipFile.OpenRead(dest);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("MySet.cardinator", entries);
            Assert.Contains("art/pic.png", entries);
            Assert.Contains("frames/my-frame/template.json", entries);
            Assert.Contains("frames/my-frame/frame.png", entries);
            Assert.DoesNotContain(entries, e => e.Contains("gold-multicolor"));  // built-in skipped
            Assert.DoesNotContain(entries, e => e.StartsWith("out/"));           // renders excluded
        }
        finally
        {
            foreach (var d in new[] { setDir, templatesDir, Path.GetDirectoryName(dest)! })
                try { Directory.Delete(d, true); } catch { }
        }
    }

    [Fact]
    public void ExportSetWithFrames_SkipsAReferencedFrameThatIsntOnDisk()
    {
        var setDir = TempDir();
        var templatesDir = TempDir();
        var dest = Path.Combine(TempDir(), "share.zip");
        try
        {
            var project = Path.Combine(setDir, "S.cardinator"); Write(project, "{}");
            int frames = SetPackager.ExportSetWithFrames(project, new[] { "Ghost Frame" }, templatesDir, dest);
            Assert.Equal(0, frames);
            Assert.True(File.Exists(dest));   // still produces a valid zip (project only)
        }
        finally
        {
            foreach (var d in new[] { setDir, templatesDir, Path.GetDirectoryName(dest)! })
                try { Directory.Delete(d, true); } catch { }
        }
    }
}
