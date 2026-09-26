using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>Unit tests for the spec-level logic that underpins cache invalidation and the royal sub-border.</summary>
public class TemplateSpecTests
{
    private static TemplateSpec Framed() => new()
    {
        Name = "T", CanvasWidth = 750, CanvasHeight = 1050, BorderThickness = 28,
        TitleBar = new() { X = 48, Y = 46, W = 654, H = 60 },
        ArtWindow = new() { X = 48, Y = 124, W = 654, H = 464 },
        TypeBar = new() { X = 48, Y = 598, W = 654, H = 56 },
        TextBox = new() { X = 48, Y = 662, W = 654, H = 296 },
    };

    // --- ContentHash (drives frame.png cache invalidation) ---

    [Fact]
    public void ContentHash_IsStable_ForClone()
    {
        var a = Framed();
        Assert.Equal(a.ContentHash(), a.Clone().ContentHash());
    }

    [Fact]
    public void ContentHash_Changes_WhenARegionMoves()
    {
        var a = Framed();
        var before = a.ContentHash();
        a.TitleBar = new() { X = 60, Y = 46, W = 654, H = 60 };
        Assert.NotEqual(before, a.ContentHash());
    }

    [Fact]
    public void ContentHash_Changes_WhenAKnobChanges()
    {
        var a = Framed();
        var before = a.ContentHash();
        a.BorderThickness = 34;
        Assert.NotEqual(before, a.ContentHash());
    }

    // --- WithSubBorderApplied ---

    [Fact]
    public void WithSubBorder_Off_ReturnsSameInstance()
    {
        var a = Framed();
        a.RoyalFrame = false;
        Assert.Same(a, a.WithSubBorderApplied());
    }

    [Fact]
    public void WithSubBorder_FullArt_IsSkipped()
    {
        var a = Framed();
        a.RoyalFrame = true; a.SubBorderThickness = 18; a.FullArt = true;
        Assert.Same(a, a.WithSubBorderApplied());
    }

    [Fact]
    public void WithSubBorder_FullBleedWindow_IsSkipped()
    {
        var a = Framed();
        a.RoyalFrame = true; a.SubBorderThickness = 18;
        a.ArtWindow = new() { X = 0, Y = 0, W = 750, H = 1050 };
        Assert.Same(a, a.WithSubBorderApplied());
    }

    [Fact]
    public void WithSubBorder_On_InsetsRegionsInward()
    {
        var a = Framed();
        a.RoyalFrame = true; a.SubBorderThickness = 18;
        var r = a.WithSubBorderApplied();

        Assert.NotSame(a, r);
        Assert.False(r.RoyalFrame);                       // copy is already inset — must not re-apply
        Assert.True(r.TitleBar.X > a.TitleBar.X);         // moved inward from the left
        Assert.True(r.TitleBar.Right < a.TitleBar.Right); // and in from the right
        Assert.True(r.TitleBar.W < a.TitleBar.W);         // narrower
    }

    // --- Normalize (repairs hand-edited / partial JSON) ---

    [Fact]
    public void Normalize_RestoresBlankStringKnobs()
    {
        var s = Framed();
        s.TaperStyle = "";
        s.FrameStyle = "";
        s.TopEmblem = null!;
        s.Texture = "";
        s.SubBorderThickness = -5;
        s.TextureStrength = -1;

        s.Normalize();

        Assert.Equal("partial", s.TaperStyle);
        Assert.Equal("classic", s.FrameStyle);
        Assert.Equal("none", s.TopEmblem);
        Assert.Equal("speckle", s.Texture);
        Assert.Equal(18, s.SubBorderThickness);
        Assert.Equal(1.0, s.TextureStrength);
    }
}
