using System;
using System.IO;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Covers the robustness polish: duplicate card-name check, the "another project already lives here"
/// save guard, and the load-time missing-art count.
/// </summary>
public class RobustnessTests
{
    private static TemplateSpec Spec() => new() { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "rob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // --- L4: duplicate card name -------------------------------------------

    [Fact]
    public void DuplicateName_IsFlagged()
    {
        var a = new CardModel { Name = "Grizzly Bears" };
        var b = new CardModel { Name = "grizzly bears" };   // case-insensitive
        Assert.Contains(CardValidator.Validate(a, Spec(), new[] { a, b }), i => i.Code == "dup-name");
    }

    [Fact]
    public void UniqueNames_AreNotFlagged()
    {
        var a = new CardModel { Name = "A" };
        var b = new CardModel { Name = "B" };
        Assert.DoesNotContain(CardValidator.Validate(a, Spec(), new[] { a, b }), i => i.Code == "dup-name");
    }

    // --- M12: folder already has another project ---------------------------

    [Fact]
    public void ContainsOtherProject_TrueWhenADifferentProjectIsPresent()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Existing.cardinator"), "{}");
            Assert.True(SetFolder.ContainsOtherProject(dir, Path.Combine(dir, "New.cardinator")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ContainsOtherProject_FalseWhenOnlyThisProjectOrEmpty()
    {
        var dir = TempDir();
        try
        {
            var self = Path.Combine(dir, "Mine.cardinator");
            File.WriteAllText(self, "{}");
            Assert.False(SetFolder.ContainsOtherProject(dir, self));   // only our own file

            var empty = TempDir();
            try { Assert.False(SetFolder.ContainsOtherProject(empty, Path.Combine(empty, "x.cardinator"))); }
            finally { try { Directory.Delete(empty, true); } catch { } }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- L8: missing-art count ---------------------------------------------

    [Fact]
    public void CountMissingArt_CountsOnlyCardsWhoseArtFileIsGone()
    {
        var dir = TempDir();
        try
        {
            var present = Path.Combine(dir, "there.png");
            File.WriteAllBytes(present, new byte[] { 1, 2, 3 });

            var cards = new[]
            {
                new CardModel { Name = "has-art", ArtPath = present },
                new CardModel { Name = "no-art", ArtPath = "" },                               // not counted
                new CardModel { Name = "gone", ArtPath = Path.Combine(dir, "missing.png") },   // counted
            };

            Assert.Equal(1, SetFolder.CountMissingArt(cards));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
